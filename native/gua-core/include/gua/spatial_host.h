#pragma once
#include "spatial.h"
#ifdef __cplusplus
extern "C" {
#endif
/* Additive spatial-host-r1 documents. Legacy document types remain strict r1. */
enum { GUA_SPATIAL_HOST_RESULT=4, GUA_SPATIAL_BATCH=5,
    GUA_SPATIAL_BATCH_RESULT=6, GUA_SPATIAL_REGISTRATION=7,
    GUA_SPATIAL_OWNER=8, GUA_SPATIAL_BOUNDARY=9, GUA_SPATIAL_EXECUTION=10,
    GUA_SPATIAL_ADVERTISEMENT=11 };
enum { GUA_SPATIAL_NOT_AUTHORIZED=8, GUA_SPATIAL_CAPACITY=9,
    GUA_SPATIAL_NOT_READY=10, GUA_SPATIAL_STALE=11 };
typedef struct gua_spatial_host_t gua_spatial_host_t;
typedef struct gua_spatial_host_options_v1_t {
    uint32_t struct_size;
    uint32_t max_providers;
    uint32_t max_owners;
    uint32_t max_queue_depth; /* includes retained completions */
    uint32_t max_queries_per_batch; /* 1..64 */
    uint32_t max_hits_per_query; /* 1..32 */
    double query_deadline_ms;
    double max_batch_work_ms;
} gua_spatial_host_options_v1_t;
/* Trusted host-only configuration; never expose these functions to clients.
 * clock_id identifies this steady-clock origin, not a physics or World clock.
 * Every API catches exceptions. C callers synchronize destroy with all calls. */
int gua_spatial_host_create(const gua_spatial_host_options_v1_t* options,
    gua_value_text_t clock_id, gua_spatial_host_t** out, gua_spatial_error_t* error);
void gua_spatial_host_destroy(gua_spatial_host_t* host);
int gua_spatial_host_register(gua_spatial_host_t*, const gua_spatial_document_t* registration,
    uint64_t* provider, gua_spatial_error_t*);
int gua_spatial_host_unregister(gua_spatial_host_t*, uint64_t provider, gua_spatial_error_t*);
int gua_spatial_host_open_owner(gua_spatial_host_t*, const gua_spatial_document_t* grants,
    uint64_t* owner, gua_spatial_error_t*);
int gua_spatial_host_set_owner(gua_spatial_host_t*, uint64_t owner,
    const gua_spatial_document_t* grants, gua_spatial_error_t*);
int gua_spatial_host_close_owner(gua_spatial_host_t*, uint64_t owner, gua_spatial_error_t*);
/* Public calls are scoped to a host-installed owner. Results are one-shot. */
int gua_spatial_host_enqueue(gua_spatial_host_t*, uint64_t owner,
    const gua_spatial_document_t* batch, gua_spatial_error_t*);
/* Public discovery projects named policies through current owner grants.
 * Unknown and forbidden provider handles both return NOT_AUTHORIZED. */
int gua_spatial_host_describe(gua_spatial_host_t*, uint64_t owner, uint64_t provider,
    gua_spatial_document_t** advertisement, gua_spatial_error_t*);
int gua_spatial_host_cancel(gua_spatial_host_t*, uint64_t owner, uint64_t batch_id, gua_spatial_error_t*);
int gua_spatial_host_poll(gua_spatial_host_t*, uint64_t owner, uint64_t batch_id,
    gua_spatial_document_t** result, gua_spatial_error_t*);
/* Trusted adapter calls: begin ONLY inside a safe physics read interval and
 * hold that state until end. No engine callbacks, locks or pointers retained.
 * take yields an owned r1 request (effective maxHits); complete accepts an owned
 * execution result. Sample is supplied by begin and stamped by core, so completed
 * adapter results omit sample on input. End is mandatory even after failure.
 * A backend call cannot be interrupted; late results are discarded truthfully. */
int gua_spatial_host_begin(gua_spatial_host_t*, uint64_t provider,
    const gua_spatial_document_t* boundary, uint64_t* lease, gua_spatial_error_t*);
int gua_spatial_host_take(gua_spatial_host_t*, uint64_t lease,
    gua_spatial_document_t** request, gua_spatial_error_t*);
int gua_spatial_host_complete(gua_spatial_host_t*, uint64_t lease,
    const gua_spatial_document_t* result, gua_spatial_error_t*);
int gua_spatial_host_end(gua_spatial_host_t*, uint64_t lease, gua_spatial_error_t*);
#ifdef __cplusplus
}
#endif
