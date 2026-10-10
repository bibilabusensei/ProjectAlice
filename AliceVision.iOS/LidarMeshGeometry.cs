using System;
using System.Runtime.InteropServices;
using ARKit;
using Foundation;
using Metal;
using SceneKit;

namespace AliceVision.iOS;

/// <summary>Copies ARKit's current mesh to a depth-only SceneKit occluder.</summary>
internal static class LidarMeshGeometry
{
    public static unsafe SCNGeometry? Create(ARMeshGeometry mesh)
    {
        try
        {
            var vertices = mesh.Vertices;
            var faces = mesh.Faces;
            int vertexCount = checked((int)vertices.Count);
            int faceCount = checked((int)faces.Count);
            long vertexOffset = checked((long)vertices.Offset);
            long vertexStride = checked((long)vertices.Stride);
            int indexSize = checked((int)faces.BytesPerIndex);
            int indicesPerFace = checked((int)faces.IndexCountPerPrimitive);
            if (vertexCount <= 0 || faceCount <= 0 || indicesPerFace != 3 ||
                vertices.Format != MTLVertexFormat.Float3 || vertices.ComponentsPerVector != 3 ||
                vertexOffset < 0 || vertexStride < 12 || indexSize is not (2 or 4))
            {
                return null;
            }

            long requiredVertexBytes = checked(vertexOffset + checked((vertexCount - 1L) * vertexStride) + 12L);
            long requiredIndexBytes = checked((long)faceCount * 3L * indexSize);
            var vertexBuffer = vertices.Buffer;
            var indexBuffer = faces.Buffer;
            var vertexAddress = vertexBuffer.Contents;
            var indexAddress = indexBuffer.Contents;
            if (vertexAddress == IntPtr.Zero || indexAddress == IntPtr.Zero ||
                requiredVertexBytes > checked((long)vertexBuffer.Length) ||
                requiredIndexBytes > checked((long)indexBuffer.Length) || requiredIndexBytes > int.MaxValue)
            {
                return null;
            }

            var copiedVertices = new SCNVector3[vertexCount];
            byte* start = (byte*)vertexAddress + vertexOffset;
            for (int i = 0; i < vertexCount; i++)
            {
                long byteOffset = checked((long)i * vertexStride);
                float* value = (float*)(start + byteOffset);
                copiedVertices[i] = new SCNVector3(value[0], value[1], value[2]);
            }
            var indices = new byte[checked((int)requiredIndexBytes)];
            Marshal.Copy(indexAddress, indices, 0, indices.Length);
            using var source = SCNGeometrySource.FromVertices(copiedVertices);
            using var indexData = NSData.FromArray(indices);
            using var element = SCNGeometryElement.FromData(indexData, SCNGeometryPrimitiveType.Triangles, faceCount, indexSize);
            var geometry = SCNGeometry.Create(new[] { source }, new[] { element });
            using var material = new SCNMaterial
            {
                ColorBufferWriteMask = SCNColorMask.None,
                ReadsFromDepthBuffer = true,
                WritesToDepthBuffer = true,
                DoubleSided = true
            };
            geometry.Materials = new[] { material };
            return geometry;
        }
        catch (OverflowException)
        {
            // Invalid native counts must not become an out-of-bounds pointer read.
            return null;
        }
    }
}
