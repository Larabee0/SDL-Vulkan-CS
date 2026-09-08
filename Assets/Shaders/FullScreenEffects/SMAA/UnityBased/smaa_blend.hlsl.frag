#include "smaa_defines.hlsl"

float4 main(VaryingsBlend i) :SV_TARGET {
    
	return SMAABlendingWeightCalculationPS (i . texcoord, i . pixcoord, i . offsets, _InputTexture, _AreaTex, _SearchTex, 0);
}