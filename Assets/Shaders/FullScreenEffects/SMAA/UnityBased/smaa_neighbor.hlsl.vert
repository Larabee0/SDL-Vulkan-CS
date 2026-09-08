#include "smaa_defines.hlsl"

VaryingsNeighbor main(Attributes v){
    VaryingsNeighbor o = VertNeighbor(v);
    return o;
}