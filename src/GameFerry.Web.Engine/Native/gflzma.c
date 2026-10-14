// A thin wrapper over the LZMA SDK's LZMA and LZMA2 decoders, called from C# through DllImport.
#include <stdlib.h>
#include "lzma/Lzma2Dec.h"

static void *GfAlloc(ISzAllocPtr p, size_t size) { (void)p; return malloc(size); }
static void GfFree(ISzAllocPtr p, void *address) { (void)p; free(address); }
static const ISzAlloc g_alloc = { GfAlloc, GfFree };

typedef struct
{
    int lzma2;
    union
    {
        CLzmaDec lzma;
        CLzma2Dec lzma2;
    } state;
} GfDecoder;

/* props: the coder properties from the 7z header (5 bytes for LZMA, 1 for LZMA2). NULL if they are refused. */
GfDecoder *gf_decoder_create(int lzma2, const unsigned char *props, int size)
{
    GfDecoder *decoder = (GfDecoder *)malloc(sizeof(GfDecoder));
    if (!decoder)
        return NULL;

    decoder->lzma2 = lzma2;
    if (lzma2)
    {
        Lzma2Dec_CONSTRUCT(&decoder->state.lzma2);
        if (size != 1 || Lzma2Dec_Allocate(&decoder->state.lzma2, props[0], &g_alloc) != SZ_OK)
        {
            free(decoder);
            return NULL;
        }
        Lzma2Dec_Init(&decoder->state.lzma2);
    }
    else
    {
        LzmaDec_CONSTRUCT(&decoder->state.lzma);
        if (LzmaDec_Allocate(&decoder->state.lzma, props, (unsigned)size, &g_alloc) != SZ_OK)
        {
            free(decoder);
            return NULL;
        }
        LzmaDec_Init(&decoder->state.lzma);
    }
    return decoder;
}

/* Decodes from src into dest; on return *srcLen and *destLen say how much of each was used. 0 on success. */
int gf_decoder_decode(GfDecoder *decoder, unsigned char *dest, size_t *destLen, const unsigned char *src, size_t *srcLen, int *status)
{
    ELzmaStatus s;
    SRes result = decoder->lzma2
        ? Lzma2Dec_DecodeToBuf(&decoder->state.lzma2, dest, destLen, src, srcLen, LZMA_FINISH_ANY, &s)
        : LzmaDec_DecodeToBuf(&decoder->state.lzma, dest, destLen, src, srcLen, LZMA_FINISH_ANY, &s);
    *status = (int)s;
    return result;
}

void gf_decoder_free(GfDecoder *decoder)
{
    if (!decoder)
        return;
    if (decoder->lzma2)
        Lzma2Dec_Free(&decoder->state.lzma2, &g_alloc);
    else
        LzmaDec_Free(&decoder->state.lzma, &g_alloc);
    free(decoder);
}
