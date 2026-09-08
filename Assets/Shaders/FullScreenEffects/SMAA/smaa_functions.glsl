

vec3 PositivePow (vec3 base, vec3 power) {return pow (abs (base), power);}
vec3 PositivePow (vec3 base, float power) {return pow (abs (base), vec3(power));}

/**
 * Gathers current pixel, and the top-left neighbors.
 */
vec3 SMAAGatherNeighbours(vec2 texcoord,
                            vec4 rtInfo,
                            vec4 offset[3],
                            sampler2D tex) {
    #ifdef SMAAGather
    return textureGather(tex, texcoord + rtInfo.xy * vec2(-0.5, -0.5)).grb;
    #else
    float P = texture(tex, texcoord).r;
    float Pleft = texture(tex, offset[0].xy).r;
    float Ptop  = texture(tex, offset[0].zw).r;
    return vec3(P, Pleft, Ptop);
    #endif
}

/**
 * Adjusts the threshold by means of predication.
 */
vec2 SMAACalculatePredicatedThreshold(vec2 texcoord,
                                        vec4 rtInfo,
                                        vec4 offset[3],
                                        sampler2D predicationTex) {
    vec3 neighbours = SMAAGatherNeighbours(texcoord, rtInfo, offset, predicationTex);
    vec2 delta = abs(neighbours.xx - neighbours.yz);
    vec2 edges = step(SMAA_PREDICATION_THRESHOLD, delta);
    return SMAA_PREDICATION_SCALE * SMAA_THRESHOLD * (1.0 - SMAA_PREDICATION_STRENGTH * edges);
}

/**
 * Conditional move:
 */
void SMAAMovc(bvec2 cond, inout vec2 variable, vec2 value) {
    SMAA_FLATTEN if (cond.x) variable.x = value.x;
    SMAA_FLATTEN if (cond.y) variable.y = value.y;
}

void SMAAMovc(bvec4 cond, inout vec4 variable, vec4 value) {
    SMAAMovc(cond.xy, variable.xy, value.xy);
    SMAAMovc(cond.zw, variable.zw, value.zw);
}
