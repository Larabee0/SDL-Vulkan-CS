
#define SMAA_HLSL_4_1
#define SMAA_AREATEX_SELECT(s) s.rg
#define SMAA_SEARCHTEX_SELECT(s) s.a
#define GAMMA_FOR_EDGE_DETECTION (1/2.2)
#define SMAA_PRESET_HIGH


layout (set = 0, binding = 0) uniform sampler LinearSampler;
layout (set = 0, binding = 1) uniform sampler PointSampler;

//layout(set = 0, binding = 2) uniform TexelSize 
//{
//	float4 value;
//} texelSize;
struct TexelSize{
    float4 value;
};
[[vk::binding(2, 0)]]
ConstantBuffer<TexelSize> texelSize;

layout (set = 0, binding = 3) uniform Texture2D uColourTexture;
layout (set = 0, binding = 4) uniform Texture2D uAreaTexture;
layout (set = 0, binding = 5) uniform Texture2D uSearchTexture;
layout (set = 0, binding = 6) uniform Texture2D uBlendTexture;
#define SMAA_RT_METRICS texelSize.value
#define _InputTexture uColourTexture
#define _AreaTex uAreaTexture
#define _SearchTex uSearchTexture
#define _BlendTex uBlendTexture
#define SAMPLE_TEXTURE2D_X_LOD(tex, LinearSampler, coord, lod) tex.SampleLevel(LinearSampler,coord,lod)

float2 ClampAndScaleUVForPoint (float2 UV)
{
	return min (UV, 1.0f) * 1.0;
}
float2 ClampAndScaleUV (float2 UV, float2 texelSize, float numberOfTexels, float2 scale)
{
	float2 maxCoord = 1.0f - numberOfTexels * texelSize;
	return min (UV, maxCoord) * scale;
}

float2 ClampAndScaleUV (float2 UV, float2 texelSize, float numberOfTexels)
{
	return ClampAndScaleUV (UV, texelSize, numberOfTexels, 1.0);
}
float2 ClampAndScaleUVForBilinear (float2 UV)
{
	return ClampAndScaleUV (UV, SMAA_RT_METRICS . zw, 0.5f);
}

float4 GetFullScreenTriangleVertexPosition (uint vertexID, float z = (1.0))
{

	float2 uv = float2 ((vertexID << 1) & 2, vertexID & 2);
	float4 pos = float4 (uv * 2.0 - 1.0, z, 1.0);
	return pos;
}
float2 GetFullScreenTriangleTexCoord (uint vertexID)
{
	return float2 ((vertexID << 1) & 2, 1.0 - (vertexID & 2));
}
float PositivePow (float base, float power) {return pow (abs (base), power);} float2 PositivePow (float2 base, float2 power) {return pow (abs (base), power);} float3 PositivePow (float3 base, float3 power) {return pow (abs (base), power);} float4 PositivePow (float4 base, float4 power) {return pow (abs (base), power);}
#include "SubpixelMorphologicalAntialiasing.hlsl"
struct Attributes
{
	uint vertexID : SV_VertexID;

};
struct VaryingsEdge
{
    float4 vertex : SV_POSITION;
    float2 texcoord : TEXCOORD0;
    float4 offsets[3] : TEXCOORD1;
};

VaryingsEdge VertEdge(Attributes v)
{
    VaryingsEdge o;
    o.vertex = GetFullScreenTriangleVertexPosition(v.vertexID);
    o.texcoord = GetFullScreenTriangleTexCoord(v.vertexID);

    SMAAEdgeDetectionVS(o.texcoord, o.offsets);

    return o;
}


struct VaryingsBlend
{
	float4 vertex : SV_POSITION;
	float2 texcoord : TEXCOORD0;
	float2 pixcoord : TEXCOORD1;
	float4 offsets [3] : TEXCOORD2;

};

VaryingsBlend VertBlend (Attributes v)
{
	VaryingsBlend o;
	o . vertex = GetFullScreenTriangleVertexPosition (v . vertexID);
	o . texcoord = GetFullScreenTriangleTexCoord (v . vertexID);

	SMAABlendingWeightCalculationVS (o . texcoord, o . pixcoord, o . offsets);

	return o;
}

struct VaryingsNeighbor
{
	float4 vertex : SV_POSITION;
	float2 texcoord : TEXCOORD0;
	float4 offset : TEXCOORD1;

};
VaryingsNeighbor VertNeighbor (Attributes v)
{
	VaryingsNeighbor o;
	o . vertex = GetFullScreenTriangleVertexPosition (v . vertexID);
	o . texcoord = GetFullScreenTriangleTexCoord (v . vertexID);

	SMAANeighborhoodBlendingVS (o . texcoord, o . offset);
	return o;
}