#include "smaa_defines.hlsl"

float4 main(VaryingsEdge i) :SV_TARGET {
    
    return float4(SMAAColorEdgeDetectionPS(i.texcoord, i.offsets, _InputTexture), 0.0, 0.0);
    
}