#pragma once
#include "gua.h"
#include "value.h"
#ifdef __cplusplus
extern "C" {
#endif
/* All status-returning APIs return zero on success. IDs are context-local,
 * never reused, and invalidated by every successful reset. Context destruction
 * must be synchronized with calls, as for other context APIs. */
enum { GUA_OBSERVE_UI = 1, GUA_OBSERVE_OBJECT = 2, GUA_OBSERVE_WORLD = 3 };
enum { GUA_OBSERVE_OK = 0, GUA_OBSERVE_ARGUMENT = 1, GUA_OBSERVE_STALE = 2,
    GUA_OBSERVE_DUPLICATE = 3, GUA_OBSERVE_NO_FRAME = 4, GUA_OBSERVE_INTERNAL = 5 };
enum { GUA_OBSERVE_GETTER_FAILED = 100, GUA_OBSERVE_NOT_SAMPLED = 101,
    GUA_OBSERVE_SENSITIVE = 102 };
typedef struct gua_observe_registration_v1_t {
    uint32_t struct_size;
    uint64_t owner_id;
    gua_value_text_t name;
    int32_t allow_player;
    int32_t sensitive;
} gua_observe_registration_v1_t;
typedef struct gua_observe_result_t gua_observe_result_t;
/* UI/object owners must name an already published node. World uses empty ID.
 * Only one live owner may bind a given source/runtime ID. */
int gua_observe_create_owner(gua_context_t*, int source, gua_value_text_t runtime_id, uint64_t* owner);
int gua_observe_destroy_owner(gua_context_t*, uint64_t owner);
int gua_observe_register_v1(gua_context_t*, const gua_observe_registration_v1_t*, uint64_t* registration);
int gua_observe_registration_alive(gua_context_t*, uint64_t registration);
int gua_observe_unregister(gua_context_t*, uint64_t registration);
/* error=0 requires an immutable Value. Otherwise value must be NULL and error
 * is a Value error code (1..11) or GETTER_FAILED. stage=1 attaches the sample
 * to the current source frame; stage=0 publishes immediately. Inputs copied. */
int gua_observe_publish(gua_context_t*, uint64_t registration, const gua_value_t* value, int error, int stage);
int gua_observe_set_limits(gua_context_t*, uint32_t events, uint64_t bytes);
/* Immutable result handles make size/copy safe even while publishers run.
 * Subscribe captures Snapshot and cursor in one context lock. */
int gua_observe_snapshot(gua_context_t*, int profile, gua_observe_result_t**);
int gua_observe_subscribe(gua_context_t*, int profile, uint64_t* subscription, gua_observe_result_t** snapshot);
int gua_observe_poll(gua_context_t*, uint64_t subscription, gua_observe_result_t**);
int gua_observe_unsubscribe(gua_context_t*, uint64_t subscription);
int gua_observe_result_copy_json(const gua_observe_result_t*, char*, int capacity);
void gua_observe_result_destroy(gua_observe_result_t*);
#ifdef __cplusplus
}
#endif
