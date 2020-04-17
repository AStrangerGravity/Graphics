using System.IO;
using Unity.Collections;
using UnityEngine;

namespace UnityEditor.Rendering
{
    // Photometric type coordinate system references:
    // https://www.ies.org/product/approved-method-guide-to-goniometer-measurements-and-types-and-photometric-coordinate-systems/
    // https://support.agi32.com/support/solutions/articles/22000209748-type-a-type-b-and-type-c-photometry
    public abstract class IesEngine
    {
        const float k_HalfPi = 0.5f*Mathf.PI;
        const float k_TwoPi  = 2.0f*Mathf.PI;

        //// k_MinTextureSize should be 32, but using a larger value to minimize Unity's issue with cubemap cookies made from low-resolution latitude-longitude images.
        //// When used, such a cubemap cookie North-South axis is visually tilted compared to its point light Y axis.
        //// In other words, when the light Y rotation is modified, the cookie highlights and shadows wriggles on the floor and walls.
        //const int k_MinTextureSize = 256; // power of two >= 32
        //const int k_MaxTextureSize = 2048; // power of two <= 2048

        //const int k_CylindricalTextureHeight = 256;                            // for 180 latitudinal degrees
        //const int k_CylindricalTextureWidth  = 2*k_CylindricalTextureHeight; // for 360 longitudinal degrees

        public IesReader m_IesReader = new IesReader();

        public string FileFormatVersion { get => m_IesReader.FileFormatVersion; }

        public TextureImporterType m_TextureGenerationType = TextureImporterType.Cookie;
        public TextureImporterType TextureGenerationType
        {
            set { m_TextureGenerationType = value; }
        }

        public string ReadFile(string iesFilePath)
        {
            if (!File.Exists(iesFilePath))
            {
                return "IES file does not exist.";
            }

            string errorMessage;

            try
            {
                errorMessage = m_IesReader.ReadFile(iesFilePath);
            }
            catch (IOException ioEx)
            {
                return ioEx.Message;
            }

            return errorMessage;
        }

        public string GetKeywordValue(string keyword)
        {
            return m_IesReader.GetKeywordValue(keyword);
        }

        public string GetPhotometricType()
        {
            switch (m_IesReader.PhotometricType)
            {
                case 3: // type A
                    return "Type A";
                case 2: // type B
                    return "Type B";
                default: // type C
                    return "Type C";
            }
        }

        public (float, string) GetMaximumIntensity()
        {
            if (m_IesReader.TotalLumens == -1f) // absolute photometry
            {
                return (m_IesReader.MaxCandelas, "Candelas");
            }
            else
            {
                return (m_IesReader.TotalLumens, "Lumens");
            }
        }

        public abstract (string, Texture) GenerateCubeCookie(TextureImporterCompression compression);

        // Gnomonic projection reference:
        // http://speleotrove.com/pangazer/gnomonic_projection.html
        public abstract(string, Texture) Generate2DCookie(TextureImporterCompression compression, float coneAngle, int textureSize, bool applyLightAttenuation);

        public abstract (string, Texture) GenerateCylindricalTexture(TextureImporterCompression compression);

        public abstract (string, Texture) GenerateTexture(TextureImporterType type, TextureImporterShape shape, TextureImporterCompression compression, int width, int height, NativeArray<Color32> colorBuffer);

        public NativeArray<Color32> BuildTypeACylindricalTexture(int width, int height)
        {
            float stepU = 360f / (width - 1);
            float stepV = 180f / (height - 1);

            var textureBuffer = new NativeArray<Color32>(width * height, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            for (int y = 0; y < height; y++)
            {
                var slice = new NativeSlice<Color32>(textureBuffer, y * width, width);

                float latitude = y * stepV - 90f; // in range [-90..+90] degrees

                float verticalAnglePosition = m_IesReader.ComputeVerticalAnglePosition(latitude);

                for (int x = 0; x < width; x++)
                {
                    float longitude = x * stepU - 180f; // in range [-180..+180] degrees

                    float horizontalAnglePosition = m_IesReader.ComputeTypeAorBHorizontalAnglePosition(longitude);

                    byte value = (byte)((m_IesReader.InterpolateBilinear(horizontalAnglePosition, verticalAnglePosition) / m_IesReader.MaxCandelas) * 255);
                    slice[x] = new Color32(value, value, value, value);
                }
            }

            return textureBuffer;
        }

        public NativeArray<Color32> BuildTypeBCylindricalTexture(int width, int height)
        {
            float stepU = k_TwoPi / (width - 1);
            float stepV = Mathf.PI / (height - 1);

            var textureBuffer = new NativeArray<Color32>(width * height, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            for (int y = 0; y < height; y++)
            {
                var slice = new NativeSlice<Color32>(textureBuffer, y * width, width);

                float v = y * stepV - k_HalfPi; // in range [-90..+90] degrees

                float sinV = Mathf.Sin(v);
                float cosV = Mathf.Cos(v);

                for (int x = 0; x < width; x++)
                {
                    float u = Mathf.PI - x * stepU; // in range [+180..-180] degrees

                    float sinU = Mathf.Sin(u);
                    float cosU = Mathf.Cos(u);

                    // Since a type B luminaire is turned on its side, rotate it to make its polar axis horizontal.
                    float longitude = Mathf.Atan2(sinV, cosU * cosV) * Mathf.Rad2Deg; // in range [-180..+180] degrees
                    float latitude = Mathf.Asin(-sinU * cosV) * Mathf.Rad2Deg;       // in range [-90..+90] degrees

                    float horizontalAnglePosition = m_IesReader.ComputeTypeAorBHorizontalAnglePosition(longitude);
                    float verticalAnglePosition   = m_IesReader.ComputeVerticalAnglePosition(latitude);

                    byte value = (byte)((m_IesReader.InterpolateBilinear(horizontalAnglePosition, verticalAnglePosition) / m_IesReader.MaxCandelas) * 255);
                    slice[x] = new Color32(value, value, value, value);
                }
            }

            return textureBuffer;
        }

        public NativeArray<Color32> BuildTypeCCylindricalTexture(int width, int height)
        {
            float stepU = k_TwoPi / (width - 1);
            float stepV = Mathf.PI / (height - 1);

            var textureBuffer = new NativeArray<Color32>(width * height, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            for (int y = 0; y < height; y++)
            {
                var slice = new NativeSlice<Color32>(textureBuffer, y * width, width);

                float v = y * stepV - k_HalfPi; // in range [-90..+90] degrees

                float sinV = Mathf.Sin(v);
                float cosV = Mathf.Cos(v);

                for (int x = 0; x < width; x++)
                {
                    float u = Mathf.PI - x * stepU; // in range [+180..-180] degrees

                    float sinU = Mathf.Sin(u);
                    float cosU = Mathf.Cos(u);

                    // Since a type C luminaire is generally aimed at nadir, orient it toward +Z at the center of the cylindrical texture.
                    float longitude = ((Mathf.Atan2(sinU * cosV, sinV) + k_TwoPi) % k_TwoPi) * Mathf.Rad2Deg; // in range [0..360] degrees
                    float latitude = (Mathf.Asin(-cosU * cosV) + k_HalfPi) * Mathf.Rad2Deg;                  // in range [0..180] degrees

                    float horizontalAnglePosition = m_IesReader.ComputeTypeCHorizontalAnglePosition(longitude);
                    float verticalAnglePosition   = m_IesReader.ComputeVerticalAnglePosition(latitude);

                    byte value = (byte)((m_IesReader.InterpolateBilinear(horizontalAnglePosition, verticalAnglePosition) / m_IesReader.MaxCandelas) * 255);
                    slice[x] = new Color32(value, value, value, value);
                }
            }

            return textureBuffer;
        }

        public NativeArray<Color32> BuildTypeAGnomonicTexture(float coneAngle, int size, bool applyLightAttenuation)
        {
            float limitUV = Mathf.Tan(0.5f * coneAngle * Mathf.Deg2Rad);
            float stepUV = (2 * limitUV) / (size - 3);

            var textureBuffer = new NativeArray<Color32>(size * size, Allocator.Temp, NativeArrayOptions.ClearMemory);

            // Leave a one-pixel black border around the texture to avoid cookie spilling.
            for (int y = 1; y < size - 1; y++)
            {
                var slice = new NativeSlice<Color32>(textureBuffer, y * size, size);

                float v = (y - 1) * stepUV - limitUV;

                for (int x = 1; x < size - 1; x++)
                {
                    float u = (x - 1) * stepUV - limitUV;

                    float rayLengthSquared = u * u + v * v + 1;

                    float longitude = Mathf.Atan(u) * Mathf.Rad2Deg;                                // in range [-90..+90] degrees
                    float latitude = Mathf.Asin(v / Mathf.Sqrt(rayLengthSquared)) * Mathf.Rad2Deg; // in range [-90..+90] degrees

                    float horizontalAnglePosition = m_IesReader.ComputeTypeCHorizontalAnglePosition(longitude);
                    float verticalAnglePosition   = m_IesReader.ComputeVerticalAnglePosition(latitude);

                    // Factor in the light attenuation further from the texture center.
                    float lightAttenuation = applyLightAttenuation ? rayLengthSquared : 1f;

                    byte value = (byte)((m_IesReader.InterpolateBilinear(horizontalAnglePosition, verticalAnglePosition) / (m_IesReader.MaxCandelas * lightAttenuation)) * 255);
                    slice[x] = new Color32(value, value, value, value);
                }
            }

            return textureBuffer;
        }

        public NativeArray<Color32> BuildTypeBGnomonicTexture(float coneAngle, int size, bool applyLightAttenuation)
        {
            float limitUV = Mathf.Tan(0.5f * coneAngle * Mathf.Deg2Rad);
            float stepUV = (2 * limitUV) / (size - 3);

            var textureBuffer = new NativeArray<Color32>(size * size, Allocator.Temp, NativeArrayOptions.ClearMemory);

            // Leave a one-pixel black border around the texture to avoid cookie spilling.
            for (int y = 1; y < size - 1; y++)
            {
                var slice = new NativeSlice<Color32>(textureBuffer, y * size, size);

                float v = (y - 1) * stepUV - limitUV;

                for (int x = 1; x < size - 1; x++)
                {
                    float u = (x - 1) * stepUV - limitUV;

                    float rayLengthSquared = u * u + v * v + 1;

                    // Since a type B luminaire is turned on its side, U and V are flipped.
                    float longitude = Mathf.Atan(v) * Mathf.Rad2Deg;                                // in range [-90..+90] degrees
                    float latitude = Mathf.Asin(u / Mathf.Sqrt(rayLengthSquared)) * Mathf.Rad2Deg; // in range [-90..+90] degrees

                    float horizontalAnglePosition = m_IesReader.ComputeTypeCHorizontalAnglePosition(longitude);
                    float verticalAnglePosition   = m_IesReader.ComputeVerticalAnglePosition(latitude);

                    // Factor in the light attenuation further from the texture center.
                    float lightAttenuation = applyLightAttenuation ? rayLengthSquared : 1f;

                    byte value = (byte)((m_IesReader.InterpolateBilinear(horizontalAnglePosition, verticalAnglePosition) / (m_IesReader.MaxCandelas * lightAttenuation)) * 255);
                    slice[x] = new Color32(value, value, value, value);
                }
            }

            return textureBuffer;
        }

        public NativeArray<Color32> BuildTypeCGnomonicTexture(float coneAngle, int size, bool applyLightAttenuation)
        {
            float limitUV = Mathf.Tan(0.5f * coneAngle * Mathf.Deg2Rad);
            float stepUV = (2 * limitUV) / (size - 3);

            var textureBuffer = new NativeArray<Color32>(size * size, Allocator.Temp, NativeArrayOptions.ClearMemory);

            // Leave a one-pixel black border around the texture to avoid cookie spilling.
            for (int y = 1; y < size - 1; y++)
            {
                var slice = new NativeSlice<Color32>(textureBuffer, y * size, size);

                float v = (y - 1) * stepUV - limitUV;

                for (int x = 1; x < size - 1; x++)
                {
                    float u = (x - 1) * stepUV - limitUV;

                    float uvLength = Mathf.Sqrt(u * u + v * v);

                    float longitude = ((Mathf.Atan2(v, u) - k_HalfPi + k_TwoPi) % k_TwoPi) * Mathf.Rad2Deg; // in range [0..360] degrees
                    float latitude = Mathf.Atan(uvLength) * Mathf.Rad2Deg;                                 // in range [0..90] degrees

                    float horizontalAnglePosition = m_IesReader.ComputeTypeCHorizontalAnglePosition(longitude);
                    float verticalAnglePosition   = m_IesReader.ComputeVerticalAnglePosition(latitude);

                    // Factor in the light attenuation further from the texture center.
                    float lightAttenuation = applyLightAttenuation ? (uvLength * uvLength + 1) : 1f;

                    byte value = (byte)((m_IesReader.InterpolateBilinear(horizontalAnglePosition, verticalAnglePosition) / (m_IesReader.MaxCandelas * lightAttenuation)) * 255);
                    slice[x] = new Color32(value, value, value, value);
                }
            }

            return textureBuffer;
        }
    }
}
