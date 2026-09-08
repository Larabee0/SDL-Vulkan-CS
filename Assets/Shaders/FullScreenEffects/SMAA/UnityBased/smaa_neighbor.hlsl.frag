#include "smaa_defines.hlsl"

float4 main(VaryingsNeighbor i) :SV_TARGET {
    
	return SMAANeighborhoodBlendingPS (i . texcoord, i . offset, _InputTexture, _BlendTex);
    
}