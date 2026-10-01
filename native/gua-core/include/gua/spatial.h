#pragma once
#include "value.h"
#ifdef __cplusplus
extern "C" {
#endif
/* Spatial-r1 offline contract documents. These APIs neither execute physics nor
 * authorize a query. No runtime capability is enabled by parsing a document. */
typedef struct gua_spatial_document_t gua_spatial_document_t;
enum { GUA_SPATIAL_REQUEST = 1, GUA_SPATIAL_RESULT = 2, GUA_SPATIAL_PROVIDER = 3 };
enum { GUA_SPATIAL_OK = 0, GUA_SPATIAL_INVALID = 1, GUA_SPATIAL_VERSION = 2,
    GUA_SPATIAL_GEOMETRY = 3, GUA_SPATIAL_CONTEXT = 4, GUA_SPATIAL_UNSUPPORTED = 5,
    GUA_SPATIAL_SEMANTICS = 6, GUA_SPATIAL_INTERNAL = 7 };
typedef struct gua_spatial_error_t { int32_t code; char path[128]; } gua_spatial_error_t;
typedef struct gua_spatial_parse_options_v1_t {
    uint32_t struct_size;
    int32_t document_type;
} gua_spatial_parse_options_v1_t;
/* All fields, including optional evidence, are carried losslessly by owned,
 * length-delimited UTF-8 JSON. Numeric wire values are finite binary64; sequence
 * metadata uses non-negative JSON safe integers. No engine pointers or handles.
 * Failed parse sets *out_document=NULL. struct_size must equal the v1 size. */
int gua_spatial_from_json(const gua_spatial_parse_options_v1_t* options,
    gua_value_text_t json, gua_spatial_document_t** out_document, gua_spatial_error_t* error);
int gua_spatial_document_type(const gua_spatial_document_t* document);
/* Required bytes include NUL; NULL/0 sizes it. Short buffers are cleared. */
int gua_spatial_copy_json(const gua_spatial_document_t* document, char* buffer, int capacity);
void gua_spatial_destroy(gua_spatial_document_t* document);
/* Context/capability matching, separate from syntax. Same space/epoch and named
 * policy are mandatory. This is NOT host authorization (#132). */
int gua_spatial_check_request(const gua_spatial_document_t* request,
    const gua_spatial_document_t* provider, gua_spatial_error_t* error);
/* Checks correlation and geometric evidence against the original request. */
int gua_spatial_check_result(const gua_spatial_document_t* request,
    const gua_spatial_document_t* result, gua_spatial_error_t* error);
#ifdef __cplusplus
}
#endif
