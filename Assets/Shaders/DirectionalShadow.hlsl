[[vk::binding(0, 3)]]
Texture2D<float4> BindlessImages[] : register(t0, space3);

[[vk::binding(1, 3)]]
SamplerState BindlessSamplers[] : register(s0, space3);

[[vk::binding(2, 3)]]
ByteAddressBuffer BindlessBuffers[] : register(t2, space3);

[[vk::push_constant]]
struct
{
    float4 modelViewProjectionColumn0;
    float4 modelViewProjectionColumn1;
    float4 modelViewProjectionColumn2;
    float4 modelViewProjectionColumn3;
    float4 baseColorFactor;
    uint baseColorImageIndex;
    uint baseColorSamplerIndex;
    float alphaCutoff;
    uint alphaTest;
    uint skinPaletteBufferIndex;
    uint skinPaletteOffset;
} DrawConstants;

// The vertex layout mirrors GenericRP/StandardLit's: a skinned draw appends joint indices and
// weights to the same static attributes, so both passes declare the same input order and bind the
// same stub layouts.
struct VSInput
{
    float3 Position : POSITION0;
    float3 Normal : NORMAL0;
    float4 Tangent : TANGENT0;
    float2 UV : TEXCOORD0;
    float3 Color : COLOR0;
#if SKINNED
    uint4 Joints : TEXCOORD5;
    float4 Weights : TEXCOORD6;
#endif
};

struct VSOutput
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
};

#if SKINNED
// Palette matrices are uploaded as four float4 rows, so row 0 holds (M11, M21, M31, M41); the
// vertex shader rebuilds the row-vector matrix from them.
float4 LoadSkinVector(uint bufferIndex, uint byteOffset)
{
    return asfloat(BindlessBuffers[
        NonUniformResourceIndex(bufferIndex)].Load4(byteOffset));
}

float4x4 LoadSkinMatrix(uint bufferIndex, uint matrixIndex)
{
    uint baseOffset = matrixIndex * 64u;
    // The palette stores a matrix the way the CPU composes it in its row-vector convention:
    // float4 #0 is (M11, M21, M31, M41), i.e. the matrix's first column. float4x4(...) takes
    // its arguments as rows, so the loaded vectors are gathered element-wise into the rows
    // mul(v, M) consumes.
    float4 column0 = LoadSkinVector(bufferIndex, baseOffset + 0u);
    float4 column1 = LoadSkinVector(bufferIndex, baseOffset + 16u);
    float4 column2 = LoadSkinVector(bufferIndex, baseOffset + 32u);
    float4 column3 = LoadSkinVector(bufferIndex, baseOffset + 48u);
    return float4x4(
        float4(column0.x, column1.x, column2.x, column3.x),
        float4(column0.y, column1.y, column2.y, column3.y),
        float4(column0.z, column1.z, column2.z, column3.z),
        float4(column0.w, column1.w, column2.w, column3.w));
}

float4x4 BuildSkinMatrix(VSInput input)
{
    uint paletteBufferIndex = DrawConstants.skinPaletteBufferIndex;
    uint paletteOffset = DrawConstants.skinPaletteOffset;
    return input.Weights.x * LoadSkinMatrix(paletteBufferIndex, paletteOffset + input.Joints.x)
        + input.Weights.y * LoadSkinMatrix(paletteBufferIndex, paletteOffset + input.Joints.y)
        + input.Weights.z * LoadSkinMatrix(paletteBufferIndex, paletteOffset + input.Joints.z)
        + input.Weights.w * LoadSkinMatrix(paletteBufferIndex, paletteOffset + input.Joints.w);
}
#endif

VSOutput VSMain(VSInput input)
{
    float4 localPosition = float4(input.Position, 1.0);
#if SKINNED
    // Shadow casters deform exactly like the lit draw does, so a skinned mesh's shadow follows the
    // pose instead of its bind pose.
    localPosition = mul(localPosition, BuildSkinMatrix(input));
#endif
    VSOutput output;
    output.Position = float4(
        dot(localPosition, DrawConstants.modelViewProjectionColumn0),
        dot(localPosition, DrawConstants.modelViewProjectionColumn1),
        dot(localPosition, DrawConstants.modelViewProjectionColumn2),
        dot(localPosition, DrawConstants.modelViewProjectionColumn3));
    output.UV = input.UV;
    return output;
}

void PSMain(VSOutput input)
{
    if (DrawConstants.alphaTest == 0)
    {
        return;
    }

    float alpha = BindlessImages[
        NonUniformResourceIndex(DrawConstants.baseColorImageIndex)].Sample(
            BindlessSamplers[
                NonUniformResourceIndex(DrawConstants.baseColorSamplerIndex)],
            input.UV).a * DrawConstants.baseColorFactor.a;
    clip(alpha - DrawConstants.alphaCutoff);
}
