
shared uint gs_cacheR[128];
shared uint gs_cacheG[128];
shared uint gs_cacheB[128];


vec3 BlurPixels(vec3 a, vec3 b, vec3 c, vec3 d, vec3 e, vec3 f, vec3 g, vec3 h, vec3 i)
{
    return 0.27343750 * (e    )
         + 0.21875000 * (d + f)
         + 0.10937500 * (c + g)
         + 0.03125000 * (b + h)
         + 0.00390625 * (a + i);
}

void Store2Pixels(uint index, vec3 pixel1, vec3 pixel2)
{
    gs_cacheR[index] = packHalf2x16(vec2(pixel1.r, pixel2.r));
    gs_cacheG[index] = packHalf2x16(vec2(pixel1.g, pixel2.g));
    gs_cacheB[index] = packHalf2x16(vec2(pixel1.b, pixel2.b));
}

void Load2Pixels(uint index, out vec3 pixel1, out vec3 pixel2)
{
    vec2 r = unpackHalf2x16(gs_cacheR[index]);
    vec2 g = unpackHalf2x16(gs_cacheG[index]);
    vec2 b = unpackHalf2x16(gs_cacheB[index]);
    pixel1 = vec3(r.x, g.x, b.x);
    pixel2 = vec3(r.y, g.y, b.y);
}

void Store1Pixel(uint index, vec3 pixel)
{
    gs_cacheR[index] = floatBitsToUint(pixel.r);
    gs_cacheG[index] = floatBitsToUint(pixel.g);
    gs_cacheB[index] = floatBitsToUint(pixel.b);
}

void Load1Pixel(uint index, out vec3 pixel)
{
    pixel = uintBitsToFloat(uvec3(gs_cacheR[index], gs_cacheG[index], gs_cacheB[index]));
}

// Blur two pixels horizontally. This reduces LDS reads and pixel unpacking.
void BlurHorizontally(uint outIndex, uint leftMostIndex)
{
    vec3 s0, s1, s2, s3, s4, s5, s6, s7, s8, s9;
    Load2Pixels(leftMostIndex + 0, s0, s1);
    Load2Pixels(leftMostIndex + 1, s2, s3);
    Load2Pixels(leftMostIndex + 2, s4, s5);
    Load2Pixels(leftMostIndex + 3, s6, s7);
    Load2Pixels(leftMostIndex + 4, s8, s9);

    Store1Pixel(outIndex    , BlurPixels(s0, s1, s2, s3, s4, s5, s6, s7, s8));
    Store1Pixel(outIndex + 1, BlurPixels(s1, s2, s3, s4, s5, s6, s7, s8, s9));
}

void BlurVertically(uvec2 pixelCoord, uint topMostIndex)
{
    vec3 s0, s1, s2, s3, s4, s5, s6, s7, s8;
    Load1Pixel(topMostIndex     , s0);
    Load1Pixel(topMostIndex +  8, s1);
    Load1Pixel(topMostIndex + 16, s2);
    Load1Pixel(topMostIndex + 24, s3);
    Load1Pixel(topMostIndex + 32, s4);
    Load1Pixel(topMostIndex + 40, s5);
    Load1Pixel(topMostIndex + 48, s6);
    Load1Pixel(topMostIndex + 56, s7);
    Load1Pixel(topMostIndex + 64, s8);

    vec3 blurred = BlurPixels(s0, s1, s2, s3, s4, s5, s6, s7, s8);

    // Guard bands
    blurred *= all(pixelCoord , uvec2(constants.outputImageSize));

    

    // Write to the final target
    imageStore(dstTexture,ivec2(pixelCoord),vec4(blurred,1.0));
    //_OutputTexture[COORD_TEXTURE2D_X(pixelCoord)] = blurred;
}
