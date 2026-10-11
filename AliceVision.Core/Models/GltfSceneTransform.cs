using System.Numerics;

namespace AliceVision.Core.Models;

/// <summary>
/// Explicit native column-vector storage for a System.Numerics row-vector
/// transform. A source row becomes a native column. This representation avoids
/// ambiguities in SceneKit scalar constructors, which take row-ordered arguments
/// even though the underlying native matrix is stored as four columns.
/// </summary>
public readonly record struct GltfSceneTransform(Vector4 Column0, Vector4 Column1, Vector4 Column2, Vector4 Column3)
{
    public static GltfSceneTransform FromNumerics(Matrix4x4 matrix)
    {
        var transform = new GltfSceneTransform(
            new Vector4(matrix.M11, matrix.M12, matrix.M13, matrix.M14),
            new Vector4(matrix.M21, matrix.M22, matrix.M23, matrix.M24),
            new Vector4(matrix.M31, matrix.M32, matrix.M33, matrix.M34),
            new Vector4(matrix.M41, matrix.M42, matrix.M43, matrix.M44));
        if (!Finite(transform.Column0) || !Finite(transform.Column1) || !Finite(transform.Column2) || !Finite(transform.Column3))
            throw new InvalidDataException("模型矩阵包含非有限数值，无法转换为原生列向量变换。");
        return transform;
    }

    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);
}
