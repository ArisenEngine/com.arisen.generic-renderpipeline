[[vk::push_constant]]
struct
{
    float4 modelViewProjectionColumn0;
    float4 modelViewProjectionColumn1;
    float4 modelViewProjectionColumn2;
    float4 modelViewProjectionColumn3;
} DrawConstants;

struct VSInput
{
    float3 Position : POSITION0;
};

float4 VSMain(VSInput input) : SV_Position
{
    float4 localPosition = float4(input.Position, 1.0);
    return float4(
        dot(localPosition, DrawConstants.modelViewProjectionColumn0),
        dot(localPosition, DrawConstants.modelViewProjectionColumn1),
        dot(localPosition, DrawConstants.modelViewProjectionColumn2),
        dot(localPosition, DrawConstants.modelViewProjectionColumn3));
}
