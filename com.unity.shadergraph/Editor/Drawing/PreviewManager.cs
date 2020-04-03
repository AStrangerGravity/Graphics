using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor.Graphing;
using UnityEditor.Graphing.Util;
using UnityEngine.Assertions;
using UnityEngine.Rendering;
using UnityEditor.ShaderGraph.Internal;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using Unity.Profiling;


namespace UnityEditor.ShaderGraph.Drawing
{
    delegate void OnPrimaryMasterChanged();

    class PreviewManager : IDisposable
    {
        GraphData m_Graph;
        MessageManager m_Messenger;

        Dictionary<Guid, PreviewRenderData> m_RenderDatas = new Dictionary<Guid, PreviewRenderData>();      // stores all of the PreviewRendererData, mapped by node GUID
        PreviewRenderData m_MasterRenderData;                                                               // cache ref to preview renderer data for the master node

        int m_MaxNodesCompiling = 4;                                                                        // max preview shaders we want to async compile at once

        // state trackers
        HashSet<AbstractMaterialNode> m_NodesShaderChanged = new HashSet<AbstractMaterialNode>();           // nodes whose shader code has changed, this node and nodes that read from it are put into NeedRecompile
        HashSet<AbstractMaterialNode> m_NodesNeedsRecompile = new HashSet<AbstractMaterialNode>();           // nodes we need to recompile the preview shader
        HashSet<AbstractMaterialNode> m_NodesToCompile = new HashSet<AbstractMaterialNode>();               // TEMPORARY list of nodes we are kicking off async compiles for RIGHT NOW
        HashSet<AbstractMaterialNode> m_NodesCompiling = new HashSet<AbstractMaterialNode>();               // nodes currently being compiled
        HashSet<AbstractMaterialNode> m_NodesCompiled = new HashSet<AbstractMaterialNode>();                // TEMPORARY list of nodes that completed compilation JUST NOW
        HashSet<AbstractMaterialNode> m_NodesToDraw = new HashSet<AbstractMaterialNode>();                  // nodes to rebuild the texture for
        HashSet<AbstractMaterialNode> m_TimedNodes = new HashSet<AbstractMaterialNode>();                   // nodes that are dependent on a time node -- i.e. animated -- need to redraw every frame
        bool m_RefreshTimedNodes;                                                                           // flag to trigger rebuilding the list of timed nodes

        PreviewSceneResources m_SceneResources;
        Texture2D m_ErrorTexture;
        Vector2? m_NewMasterPreviewSize;

        public PreviewRenderData masterRenderData
        {
            get { return m_MasterRenderData; }
        }

        public PreviewManager(GraphData graph, MessageManager messenger)
        {
            m_Graph = graph;
            m_Messenger = messenger;
            m_ErrorTexture = GenerateFourSquare(Color.magenta, Color.black);
            m_SceneResources = new PreviewSceneResources();

            foreach (var node in m_Graph.GetNodes<AbstractMaterialNode>())
                AddPreview(node);
        }

        public OnPrimaryMasterChanged onPrimaryMasterChanged;

        static Texture2D GenerateFourSquare(Color c1, Color c2)
        {
            var tex = new Texture2D(2, 2);
            tex.SetPixel(0, 0, c1);
            tex.SetPixel(0, 1, c2);
            tex.SetPixel(1, 0, c2);
            tex.SetPixel(1, 1, c1);
            tex.filterMode = FilterMode.Point;
            tex.Apply();
            return tex;
        }

        public void ResizeMasterPreview(Vector2 newSize)
        {
            m_NewMasterPreviewSize = newSize;
        }

        public PreviewRenderData GetPreview(AbstractMaterialNode node)
        {
            return m_RenderDatas[node.guid];
        }

        void AddPreview(AbstractMaterialNode node)
        {
            var isMaster = false;

            if (node is IMasterNode || node is SubGraphOutputNode)
            {
                if (masterRenderData != null || (node is IMasterNode && node.guid != node.owner.activeOutputNodeGuid))
                {
                    return;
                }

                isMaster = true;
            }

            var renderData = new PreviewRenderData
            {
                renderTexture =
                    new RenderTexture(200, 200, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    }
            };

            if (isMaster)
            {
                m_MasterRenderData = renderData;
                renderData.renderTexture.width = renderData.renderTexture.height = 400;
            }

            renderData.renderTexture.Create();

            var shaderData = new PreviewShaderData
            {
                node = node,
                passesCompiling = 0,
                isOutOfDate = true,
                hasError = false,
            };
            renderData.shaderData = shaderData;

            m_RenderDatas.Add(node.guid, renderData);
            node.RegisterCallback(OnNodeModified);

            if (node.RequiresTime())
            {
                m_RefreshTimedNodes = true;
            }

            if (m_MasterRenderData == renderData && onPrimaryMasterChanged != null)
            {
                onPrimaryMasterChanged();
            }

            m_NodesNeedsRecompile.Add(node);
        }

        void OnNodeModified(AbstractMaterialNode node, ModificationScope scope)
        {
            if (scope == ModificationScope.Topological ||
                scope == ModificationScope.Graph)
            {
                m_NodesShaderChanged.Add(node);
                m_RefreshTimedNodes = true;
            }
            else if (scope == ModificationScope.Node)
            {
                // if we only changed a constant on the node, we don't have to rebuild the shader for it, just re-render it with the updated constant
                m_NodesToDraw.Add(node);
            }
        }

        static Stack<AbstractMaterialNode> m_NodeWave = new Stack<AbstractMaterialNode>();
        static HashSet<AbstractMaterialNode> m_AddedToNodeWave = new HashSet<AbstractMaterialNode>();
        static List<AbstractMaterialNode> m_NextLevelNodes = new List<AbstractMaterialNode>();

        // cache the Action to avoid GC
        Action<AbstractMaterialNode> AddNextLevelNodesToWave =
            nextLevelNode =>
            {
                if (!m_AddedToNodeWave.Contains(nextLevelNode))
                {
                    m_NodeWave.Push(nextLevelNode);
                    m_AddedToNodeWave.Add(nextLevelNode);
                }
            };

        enum PropagationDirection
        {
            Upstream,
            Downstream
        }

        // adds all nodes in sources, and all nodes in the given direction relative to them, into result
        // sources and result can be the same HashSet (maybe?)
        void PropagateNodes(HashSet<AbstractMaterialNode> sources, PropagationDirection dir, HashSet<AbstractMaterialNode> result)
        {
            // NodeWave represents the list of nodes we still have to process
            m_NodeWave.Clear();
            m_AddedToNodeWave.Clear();
            foreach (var node in sources)
            {
                m_NodeWave.Push(node);
                m_AddedToNodeWave.Add(node);
            }

            while (m_NodeWave.Count > 0)
            {
                var node = m_NodeWave.Pop();
                if (node == null)
                    continue;

                result.Add(node);

                // grab connected nodes in propagation direction, add them to the node wave
                ForeachConnectedNode(node, dir, AddNextLevelNodesToWave);
            }

            // clean up any temp data
            m_NodeWave.Clear();
            m_AddedToNodeWave.Clear();
        }

        void PropagateNodeList<T>(T nodes, PropagationDirection dir) where T : ICollection<AbstractMaterialNode>
        {
            // TODO: I think this algorithm is technically correct
            // but potentially traverses the same nodes MANY times in a large graph

            m_NodeWave.Clear();
            foreach (var node in nodes)
                m_NodeWave.Push(node);

            while (m_NodeWave.Count > 0)
            {
                var node = m_NodeWave.Pop();
                if (node == null)
                    continue;

                m_NextLevelNodes.Clear();
                GetConnectedNodes(node, dir, m_NextLevelNodes);

                foreach (var nextNode in m_NextLevelNodes)
                {
                    nodes.Add(nextNode);
                    m_NodeWave.Push(nextNode);
                }
            }

            // clean up any temp data
            m_NodeWave.Clear();
            m_NextLevelNodes.Clear();
        }

        List<IEdge> m_Edges = new List<IEdge>();
        List<MaterialSlot> m_Slots = new List<MaterialSlot>();

        void ForeachConnectedNode(AbstractMaterialNode node, PropagationDirection dir, Action<AbstractMaterialNode> action)
        {
            // Could write list-less action based iterators:
            // node.ForEachOutputSlot<MaterialSlot>(ApplyActionAcrossConnectedSlot, graph, action);

            // Loop through all nodes that the node feeds into.
            m_Slots.Clear();                                // serializing this into a list makes me hurt inside
            if (dir == PropagationDirection.Downstream)
                node.GetOutputSlots(m_Slots);
            else
                node.GetInputSlots(m_Slots);

            foreach (var slot in m_Slots)
            {
                // get the edges out of each slot
                m_Edges.Clear();                            // and here we serialize another list, ouch!
                m_Graph.GetEdges(slot.slotReference, m_Edges);
                foreach (var edge in m_Edges)
                {
                    // We look at each node we feed into.
                    var connectedSlot = (dir == PropagationDirection.Downstream) ? edge.inputSlot : edge.outputSlot;
                    var connectedNodeGuid = connectedSlot.nodeGuid;
                    var connectedNode = m_Graph.GetNodeFromGuid(connectedNodeGuid);

                    action(connectedNode);
                }
            }

            // clean up temp data
            m_Slots.Clear();
            m_Edges.Clear();
        }

        void GetConnectedNodes<T>(AbstractMaterialNode node, PropagationDirection dir, T connections) where T : ICollection<AbstractMaterialNode>
        {
            // Loop through all nodes that the node feeds into.
            m_Slots.Clear();
            if (dir == PropagationDirection.Downstream)
                node.GetOutputSlots(m_Slots);
            else
                node.GetInputSlots(m_Slots);
            foreach (var slot in m_Slots)
            {
                // get the edges out of each slot
                m_Edges.Clear();
                m_Graph.GetEdges(slot.slotReference, m_Edges);
                foreach (var edge in m_Edges)
                {
                    // We look at each node we feed into.
                    var connectedSlot = (dir == PropagationDirection.Downstream) ? edge.inputSlot : edge.outputSlot;
                    var connectedNodeGuid = connectedSlot.nodeGuid;
                    var connectedNode = m_Graph.GetNodeFromGuid(connectedNodeGuid);

                    // If the input node is already in the set, we don't need to process it.
                    if (connections.Contains(connectedNode))        // NOTE: this is expensive with a list...
                        continue;

                    // Add the node to the set, and to the wavefront such that we can process the nodes that it feeds into.
                    connections.Add(connectedNode);
                }
            }

            // clean up temp data
            m_Slots.Clear();
            m_Edges.Clear();
        }

        public bool HandleGraphChanges()
        {
            if (m_Graph.didActiveOutputNodeChange)
            {
                DestroyPreview(masterRenderData.shaderData.node.guid);
            }

            foreach (var node in m_Graph.removedNodes)
            {
                DestroyPreview(node.guid);
                m_RefreshTimedNodes = true;
            }

            // remove the nodes from the state trackers
            m_NodesShaderChanged.ExceptWith(m_Graph.removedNodes);
            m_NodesNeedsRecompile.ExceptWith(m_Graph.removedNodes);
            m_NodesToCompile.ExceptWith(m_Graph.removedNodes);
            m_NodesCompiling.ExceptWith(m_Graph.removedNodes);
            m_NodesCompiled.ExceptWith(m_Graph.removedNodes);
            m_NodesToDraw.ExceptWith(m_Graph.removedNodes);
            m_TimedNodes.ExceptWith(m_Graph.removedNodes);

            m_Messenger.ClearNodesFromProvider(this, m_Graph.removedNodes);

            foreach (var node in m_Graph.addedNodes)
            {
                AddPreview(node);
                m_RefreshTimedNodes = true;
            }

            foreach (var edge in m_Graph.removedEdges)
            {
                var node = m_Graph.GetNodeFromGuid(edge.inputSlot.nodeGuid);
                if (node != null)
                {
                    m_NodesShaderChanged.Add(node);
                    m_RefreshTimedNodes = true;
                }
            }
            foreach (var edge in m_Graph.addedEdges)
            {
                var node = m_Graph.GetNodeFromGuid(edge.inputSlot.nodeGuid);
                if(node != null)
                {
                    m_NodesShaderChanged.Add(node);
                    m_RefreshTimedNodes = true;
                }
            }

            // TODO: what exactly does this bool return control??       Seems to only control whether we rebuild colors or not...
            return (m_NodesNeedsRecompile.Count > 0) || (m_NodesShaderChanged.Count > 0);
        }

        List<PreviewProperty> m_PreviewProperties = new List<PreviewProperty>();
        List<AbstractMaterialNode> m_PropertyNodes = new List<AbstractMaterialNode>();

        void CollectShaderProperties(AbstractMaterialNode node, PreviewRenderData renderData)
        {
            m_PreviewProperties.Clear();
            m_PropertyNodes.Clear();

            m_PropertyNodes.Add(node);
            PropagateNodeList(m_PropertyNodes, PropagationDirection.Upstream);

            foreach (var propNode in m_PropertyNodes)
            {
                propNode.CollectPreviewMaterialProperties(m_PreviewProperties);
            }

            foreach (var prop in m_Graph.properties)
                m_PreviewProperties.Add(prop.GetPreviewMaterialProperty());

            foreach (var previewProperty in m_PreviewProperties)
                renderData.shaderData.mat.SetPreviewProperty(previewProperty);
        }

        List<PreviewRenderData> m_RenderList2D = new List<PreviewRenderData>();
        List<PreviewRenderData> m_RenderList3D = new List<PreviewRenderData>();

        private static readonly ProfilerMarker RenderPreviewsMarker = new ProfilerMarker("RenderPreviews");
        private static readonly ProfilerMarker Render2DMarker = new ProfilerMarker("Render2D");
        private static readonly ProfilerMarker Render3DMarker = new ProfilerMarker("Render3D");
        public void RenderPreviews(bool requestShaders = true)
        {
            using (RenderPreviewsMarker.Auto())
            {
                if (requestShaders)
                    UpdateShaders();
                UpdateTimedNodeList();

                PropagateNodeList(m_NodesToDraw, PropagationDirection.Downstream);
                m_NodesToDraw.UnionWith(m_TimedNodes);

                var time = Time.realtimeSinceStartup;
                var timeParameters = new Vector4(time, Mathf.Sin(time), Mathf.Cos(time), 0.0f);

                foreach (var node in m_NodesToDraw)
                {
                    if (node == null || !node.hasPreview || !node.previewExpanded)
                        continue;

                    var renderData = m_RenderDatas[node.guid];

                    if ((renderData.shaderData.shader == null) || (renderData.shaderData.mat == null))
                    {
                        if (renderData.texture != null)     // avoid calling this all the time if the view already knows
                        {
                            // TODO: pretty sure this code is never hit?
                            renderData.texture = null;
                            renderData.NotifyPreviewChanged();      // force redraw node -- 
                        }
                        continue;
                    }

                    CollectShaderProperties(node, renderData);
                    renderData.shaderData.mat.SetVector("_TimeParameters", timeParameters);

                    if (renderData.shaderData.hasError)
                    {
                        renderData.texture = m_ErrorTexture;
                        renderData.NotifyPreviewChanged();
                        continue;
                    }

                    if (renderData.previewMode == PreviewMode.Preview2D)
                        m_RenderList2D.Add(renderData);
                    else
                        m_RenderList3D.Add(renderData);
                }

                EditorUtility.SetCameraAnimateMaterialsTime(m_SceneResources.camera, time);

                m_SceneResources.light0.enabled = true;
                m_SceneResources.light0.intensity = 1.0f;
                m_SceneResources.light0.transform.rotation = Quaternion.Euler(50f, 50f, 0);
                m_SceneResources.light1.enabled = true;
                m_SceneResources.light1.intensity = 1.0f;
                m_SceneResources.camera.clearFlags = CameraClearFlags.Color;

                // Render 2D previews
                m_SceneResources.camera.transform.position = -Vector3.forward * 2;
                m_SceneResources.camera.transform.rotation = Quaternion.identity;
                m_SceneResources.camera.orthographicSize = 0.5f;
                m_SceneResources.camera.orthographic = true;

                using (Render2DMarker.Auto())
                {
                    foreach (var renderData in m_RenderList2D)
                        RenderPreview(renderData, m_SceneResources.quad, Matrix4x4.identity);
                }

                // Render 3D previews
                m_SceneResources.camera.transform.position = -Vector3.forward * 5;
                m_SceneResources.camera.transform.rotation = Quaternion.identity;
                m_SceneResources.camera.orthographic = false;

                using (Render3DMarker.Auto())
                {
                    foreach (var renderData in m_RenderList3D)
                        RenderPreview(renderData, m_SceneResources.sphere, Matrix4x4.identity);
                }

                var renderMasterPreview = masterRenderData != null && m_NodesToDraw.Contains(masterRenderData.shaderData.node);
                if (renderMasterPreview && masterRenderData.shaderData.mat != null)
                {
                    CollectShaderProperties(masterRenderData.shaderData.node, masterRenderData);

                    if (m_NewMasterPreviewSize.HasValue)
                    {
                        if (masterRenderData.renderTexture != null)
                            Object.DestroyImmediate(masterRenderData.renderTexture, true);
                        masterRenderData.renderTexture = new RenderTexture((int)m_NewMasterPreviewSize.Value.x, (int)m_NewMasterPreviewSize.Value.y, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { hideFlags = HideFlags.HideAndDontSave };
                        masterRenderData.renderTexture.Create();
                        masterRenderData.texture = masterRenderData.renderTexture;
                        m_NewMasterPreviewSize = null;
                    }
                    var mesh = m_Graph.previewData.serializedMesh.mesh ? m_Graph.previewData.serializedMesh.mesh : m_SceneResources.sphere;
                    var previewTransform = Matrix4x4.Rotate(m_Graph.previewData.rotation);
                    var scale = m_Graph.previewData.scale;
                    previewTransform *= Matrix4x4.Scale(scale * Vector3.one * (Vector3.one).magnitude / mesh.bounds.size.magnitude);
                    previewTransform *= Matrix4x4.Translate(-mesh.bounds.center);

                    RenderPreview(masterRenderData, mesh, previewTransform);
                }

                m_SceneResources.light0.enabled = false;
                m_SceneResources.light1.enabled = false;

                foreach (var renderData in m_RenderList2D)
                    renderData.NotifyPreviewChanged();
                foreach (var renderData in m_RenderList3D)
                    renderData.NotifyPreviewChanged();
                if (renderMasterPreview)
                    masterRenderData.NotifyPreviewChanged();

                m_RenderList2D.Clear();
                m_RenderList3D.Clear();
                m_NodesToDraw.Clear();
            }
        }

        public void ForceShaderUpdate()
        {
            foreach (var data in m_RenderDatas.Values)
            {
                m_NodesNeedsRecompile.Add(data.shaderData.node);
            }
        }

        private static readonly ProfilerMarker UpdateShadersMarker = new ProfilerMarker("UpdateShaders");
        private static readonly ProfilerMarker CheckCompleteMarker = new ProfilerMarker("CheckComplete");
        private static readonly ProfilerMarker DirtyNodePropMarker = new ProfilerMarker("DirtyNodeProp");
        private static readonly ProfilerMarker GeneratorMarker = new ProfilerMarker("Generator");
        void UpdateShaders()
        {
            using (UpdateShadersMarker.Auto())
            {
                // Check for shaders that finished compiling and set them to redraw
                using (CheckCompleteMarker.Auto())
                {
                    m_NodesCompiled.Clear();
                    foreach (var node in m_NodesCompiling)
                    {
                        PreviewRenderData renderData = m_RenderDatas[node.guid];
                        PreviewShaderData shaderData = renderData.shaderData;
                        Assert.IsTrue(shaderData.passesCompiling > 0);

                        // sometimes there's more passes discovered after we kick the first compile
                        if (shaderData.passesCompiling < shaderData.mat.passCount)
                        {
                            // kick the rest
                            Debug.Log("PASS WEIRDNESS DETECTED, RECOMPILING PASSES: (was " + shaderData.passesCompiling + ", now " + shaderData.mat.passCount + "node: " + node.name + ")");
                            for (var i = 0; i < shaderData.mat.passCount; i++)
                            {
                                using (CompilePassMarker.Auto())
                                {
                                    ShaderUtil.CompilePass(shaderData.mat, i);
                                }
                            }
                            shaderData.passesCompiling = shaderData.mat.passCount;
                        }

                        // check that all passes have compiled
                        var allPassesCompiled = true;
                        for (var i = 0; i < renderData.shaderData.mat.passCount; i++)
                        {
                            if (!ShaderUtil.IsPassCompiled(renderData.shaderData.mat, i))
                            {
                                allPassesCompiled = false;
                                break;
                            }
                        }

                        if (!allPassesCompiled)
                        {
                            continue;
                        }

                        // Force the material to re-generate all it's shader properties, by reassigning the shader
                        // Debug.Log("Compile complete for: " + node.name);
                        renderData.shaderData.mat.shader = renderData.shaderData.shader;
                        renderData.shaderData.passesCompiling = 0;
                        renderData.shaderData.isOutOfDate = false;
                        CheckForErrors(renderData.shaderData);

                        m_NodesCompiled.Add(renderData.shaderData.node);

                        var masterNode = renderData.shaderData.node as IMasterNode;
                        if (masterNode != null)
                        {
                            Debug.Log("Master Node Complete: " + node.name + " passes: " + renderData.shaderData.mat.passCount);
                            masterNode.ProcessPreviewMaterial(renderData.shaderData.mat);
                        }
                    }

                    // removed compiled nodes from compiling list
                    m_NodesCompiling.ExceptWith(m_NodesCompiled);

                    // and add them to the draw list
                    m_NodesToDraw.UnionWith(m_NodesCompiled);
                }

                if (m_NodesShaderChanged.Count > 0)
                {
                    // nodes with shader changes cause all downstream nodes to need recompilation
                    PropagateNodes(m_NodesShaderChanged, PropagationDirection.Downstream, m_NodesNeedsRecompile);
                    m_NodesShaderChanged.Clear();
                }

                // if there's nothing to update, or if too many nodes are still compiling, then just return
                if ((m_NodesNeedsRecompile.Count == 0) || (m_NodesCompiling.Count >= m_MaxNodesCompiling))
                    return;

                // flag all nodes in m_NodesNeedRecompile as having out of date textures, and redraw them
                foreach (var node in m_NodesNeedsRecompile)
                {                    
                    PreviewRenderData previewRendererData = m_RenderDatas[node.guid];
                    if (!previewRendererData.shaderData.isOutOfDate)
                    {
                        previewRendererData.shaderData.isOutOfDate = true;
                        previewRendererData.NotifyPreviewChanged();
                    }
                }

                // select some nodes to start async compile, by populating m_NodeToCompile
                m_NodesToCompile.Clear();

                // master node compile is first in the priority list, as it takes longer than the other previews
                if ((m_NodesCompiling.Count + m_NodesToCompile.Count < m_MaxNodesCompiling) &&
                     m_NodesNeedsRecompile.Contains(m_MasterRenderData.shaderData.node) &&
                     !m_NodesCompiling.Contains(m_MasterRenderData.shaderData.node))
                {
                    Debug.Log("Kicking preview shader compile for master node: " + m_MasterRenderData.shaderData.node.name);
                    m_NodesToCompile.Add(m_MasterRenderData.shaderData.node);
                }

                // add each node to compile list if it needs a preview, is not already compiling, and we have room
                // (we don't want to double kick compiles, wait for the first one to get back before kicking another)
                if (m_NodesCompiling.Count + m_NodesToCompile.Count < m_MaxNodesCompiling)
                {
                    foreach (var node in m_NodesNeedsRecompile)
                    {
                        if (node.hasPreview && node.previewExpanded && !m_NodesCompiling.Contains(node))
                        {
                            // Debug.Log("Kicking preview shader compile for: " + node.name);
                            m_NodesToCompile.Add(node);
                            if (m_NodesCompiling.Count + m_NodesToCompile.Count >= m_MaxNodesCompiling)
                                break;
                        }
                    }
                }

                // remove the selected nodes from the recompile list
                m_NodesNeedsRecompile.ExceptWith(m_NodesToCompile);

                // Reset error states for the UI, the shader, and all render data for nodes we're recompiling
                m_Messenger.ClearNodesFromProvider(this, m_NodesToCompile);

                // Force async compile on
                var wasAsyncAllowed = ShaderUtil.allowAsyncCompilation;
                ShaderUtil.allowAsyncCompilation = true;

                // kick async compiles for all nodes in m_NodeToCompile
                foreach (var node in m_NodesToCompile)
                {
                    if (node is IMasterNode && node == masterRenderData.shaderData.node && !(node is VfxMasterNode))
                    {
                        UpdateMasterNodeShader();
                        continue;
                    }

                    Assert.IsFalse(!node.hasPreview && !(node is SubGraphOutputNode || node is VfxMasterNode));

                    var renderData = m_RenderDatas[node.guid];
                    if (renderData == null)
                    {
                        continue;
                    }

                    // Get shader code and compile
                    Generator generator;
                    using (GeneratorMarker.Auto())
                    {
                        generator = new Generator(node.owner, node, GenerationMode.Preview, $"hidden/preview/{node.GetVariableNameForNode()}");
                    }
                    BeginCompile(renderData, generator.generatedShader);

                    // TODO: Can we move this somewhere more relevant?  seems more preview rendering related
                    // Calculate the PreviewMode from upstream nodes
                    // If any upstream node is 3D that trickles downstream
                    List<AbstractMaterialNode> upstreamNodes = new List<AbstractMaterialNode>();
                    NodeUtils.DepthFirstCollectNodesFromNode(upstreamNodes, node, NodeUtils.IncludeSelf.Include);
                    renderData.previewMode = PreviewMode.Preview2D;
                    foreach (var pNode in upstreamNodes)
                    {
                        if (pNode.previewMode == PreviewMode.Preview3D)
                        {
                            renderData.previewMode = PreviewMode.Preview3D;
                            break;
                        }
                    }
                }

                ShaderUtil.allowAsyncCompilation = wasAsyncAllowed;
                m_NodesToCompile.Clear();
            }
        }

        private static readonly ProfilerMarker BeginCompileMarker = new ProfilerMarker("BeginCompile");
        private static readonly ProfilerMarker UpdateShaderAssetMarker = new ProfilerMarker("UpdateShaderAsset");
        private static readonly ProfilerMarker CompilePassMarker = new ProfilerMarker("CompilePass");
        private static readonly ProfilerMarker CreateShaderAssetMarker = new ProfilerMarker("CreateShaderAsset");
        void BeginCompile(PreviewRenderData renderData, string shaderStr, bool debug = false)
        {
            using (BeginCompileMarker.Auto())
            {
                var shaderData = renderData.shaderData;
                Assert.IsTrue(shaderData.passesCompiling == 0);     // not sure what happens if we double-launch a compile.. at the very least it is double work, possibly gets confused

                if (shaderData.shader == null)
                {
                    using (CreateShaderAssetMarker.Auto())
                    {
                        shaderData.shader = ShaderUtil.CreateShaderAsset(shaderStr, false);
                        shaderData.shader.hideFlags = HideFlags.HideAndDontSave;
                    }
                }
                else
                {
                    using (UpdateShaderAssetMarker.Auto())
                    {
                        ShaderUtil.ClearCachedData(shaderData.shader);
                        ShaderUtil.UpdateShaderAsset(shaderData.shader, shaderStr, false);
                    }
                }

                if (shaderData.mat == null)
                {
                    shaderData.mat = new Material(shaderData.shader) { hideFlags = HideFlags.HideAndDontSave };
                }

                if (debug)
                {
                    Debug.Log("Master Node BeginCompile, passes = " + shaderData.mat.passCount);
                }

                shaderData.passesCompiling = shaderData.mat.passCount;
                for (var i = 0; i < shaderData.mat.passCount; i++)
                {
                    using (CompilePassMarker.Auto())
                    {
                        ShaderUtil.CompilePass(shaderData.mat, i);
                    }
                }
                m_NodesCompiling.Add(shaderData.node);
            }
        }

        void UpdateTimedNodeList()
        {
            if (!m_RefreshTimedNodes)
                return;

            m_TimedNodes.Clear();

            foreach (var timeNode in m_Graph.GetNodes<AbstractMaterialNode>().Where(node => node.RequiresTime()))
            {
                m_TimedNodes.Add(timeNode);
            }

            PropagateNodeList(m_TimedNodes, PropagationDirection.Downstream);
            m_RefreshTimedNodes = false;
        }

        void RenderPreview(PreviewRenderData renderData, Mesh mesh, Matrix4x4 transform)
        {
            var node = renderData.shaderData.node;
            Assert.IsTrue((node != null && node.hasPreview && node.previewExpanded) || node == masterRenderData?.shaderData?.node);

            if (renderData.shaderData.hasError)
            {
                renderData.texture = m_ErrorTexture;
                return;
            }

            var previousRenderTexture = RenderTexture.active;

            //Temp workaround for alpha previews...
            var temp = RenderTexture.GetTemporary(renderData.renderTexture.descriptor);
            RenderTexture.active = temp;
            Graphics.Blit(Texture2D.whiteTexture, temp, m_SceneResources.checkerboardMaterial);

            m_SceneResources.camera.targetTexture = temp;
            Graphics.DrawMesh(mesh, transform, renderData.shaderData.mat, 1, m_SceneResources.camera, 0, null, ShadowCastingMode.Off, false, null, false);

            var previousUseSRP = Unsupported.useScriptableRenderPipeline;
            Unsupported.useScriptableRenderPipeline = renderData.shaderData.node is IMasterNode;
            m_SceneResources.camera.Render();
            Unsupported.useScriptableRenderPipeline = previousUseSRP;

            Graphics.Blit(temp, renderData.renderTexture, m_SceneResources.blitNoAlphaMaterial);
            RenderTexture.ReleaseTemporary(temp);

            RenderTexture.active = previousRenderTexture;
            renderData.texture = renderData.renderTexture;
        }

        void CheckForErrors(PreviewShaderData shaderData)
        {
            shaderData.hasError = ShaderUtil.ShaderHasError(shaderData.shader);
            if (shaderData.hasError)
            {
                var messages = ShaderUtil.GetShaderMessages(shaderData.shader);
                if (messages.Length > 0)
                {
                    m_Messenger.AddOrAppendError(this, shaderData.node.guid, messages[0]);
                }
            }
        }

        void UpdateMasterNodeShader()
        {
            var shaderData = masterRenderData?.shaderData;
            var masterNode = shaderData?.node as IMasterNode;

            if (masterNode == null)
                return;

            var generator = new Generator(m_Graph, shaderData?.node, GenerationMode.Preview, shaderData?.node.name);
            shaderData.shaderString = generator.generatedShader;

            if (string.IsNullOrEmpty(shaderData.shaderString))
            {
                if (shaderData.shader != null)
                {
                    ShaderUtil.ClearShaderMessages(shaderData.shader);
                    Object.DestroyImmediate(shaderData.shader, true);
                    shaderData.shader = null;
                }
                return;
            }

            BeginCompile(masterRenderData, shaderData.shaderString, true);
        }

        void DestroyRenderData(PreviewRenderData renderData)
        {
            if (renderData.shaderData != null)
            {
                if (renderData.shaderData.mat != null)
                {
                    Object.DestroyImmediate(renderData.shaderData.mat, true);
                }
                if (renderData.shaderData.shader != null)
                {
                    Object.DestroyImmediate(renderData.shaderData.shader, true);
                }
            }

            if (renderData.renderTexture != null)
                Object.DestroyImmediate(renderData.renderTexture, true);

            if (renderData.shaderData != null && renderData.shaderData.node != null)
                renderData.shaderData.node.UnregisterCallback(OnNodeModified);
        }

        void DestroyPreview(Guid nodeId)
        {
            if (!m_RenderDatas.TryGetValue(nodeId, out var renderData))
            {
                return;
            }

            DestroyRenderData(renderData);
            m_RenderDatas.Remove(nodeId);

            // Check if we're destroying the shader data used by the master preview
            if (masterRenderData == renderData)
            {
                m_MasterRenderData = null;
                if (!m_Graph.isSubGraph && renderData.shaderData.node.guid != m_Graph.activeOutputNodeGuid)
                {
                    AddPreview(m_Graph.outputNode);
                }

                if (onPrimaryMasterChanged != null)
                    onPrimaryMasterChanged();
            }
        }

        void ReleaseUnmanagedResources()
        {
            if (m_ErrorTexture != null)
            {
                Object.DestroyImmediate(m_ErrorTexture);
                m_ErrorTexture = null;
            }
            if (m_SceneResources != null)
            {
                m_SceneResources.Dispose();
                m_SceneResources = null;
            }
            foreach (var renderData in m_RenderDatas.Values)
                DestroyRenderData(renderData);
            m_RenderDatas.Clear();
        }

        public void Dispose()
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }

        ~PreviewManager()
        {
            throw new Exception("PreviewManager was not disposed of properly.");
        }
    }

    delegate void OnPreviewChanged();

    class PreviewShaderData
    {
        public AbstractMaterialNode node { get; set; }
        public Shader shader { get; set; }
        public Material mat { get; set; }
        public string shaderString { get; set; }
        public int passesCompiling { get; set; }
        public bool isOutOfDate { get; set; }
        public bool hasError { get; set; }
    }

    class PreviewRenderData
    {
        public PreviewShaderData shaderData { get; set; }
        public RenderTexture renderTexture { get; set; }
        public Texture texture { get; set; }
        public PreviewMode previewMode { get; set; }
        public OnPreviewChanged onPreviewChanged;

        public void NotifyPreviewChanged()
        {
            if (onPreviewChanged != null)
                onPreviewChanged();
        }
    }
}
