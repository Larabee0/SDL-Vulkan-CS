#include "smaa_defines.hlsl"

VaryingsBlend main(Attributes v){
    VaryingsBlend o = VertBlend(v);
    return o;
}