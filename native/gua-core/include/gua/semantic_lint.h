#ifndef GUA_SEMANTIC_LINT_H
#define GUA_SEMANTIC_LINT_H
#include "gua.h"
#ifdef __cplusplus
extern "C" {
#endif
typedef struct gua_semantic_lint_report gua_semantic_lint_report_t;
typedef struct gua_semantic_lint_options_v1 {
    unsigned int struct_size;
    int observation_profile;
    int include_world;
} gua_semantic_lint_options_v1_t;
/* Return 0 on success, 1 on invalid input, 2 on internal failure. No automatic lint.
 * Synchronize context access as for other core reads. Only committed frames are read. */
int gua_semantic_lint_analyze(gua_context_t* ctx, const gua_semantic_lint_options_v1_t* options,
    gua_semantic_lint_report_t** out_report);
/* Offline inspection of previously published, already projected snapshots. This
 * function does not project or authorize data; profile records the source profile.
 * ui_tree_json is required; world_tree_json may be NULL. */
int gua_semantic_lint_analyze_snapshots(const char* ui_tree_json, const char* world_tree_json,
    int observation_profile, gua_semantic_lint_report_t** out_report);
/* Immutable report: required byte count includes NUL. An undersized buffer is not written. */
int gua_semantic_lint_report_copy_json(const gua_semantic_lint_report_t* report, char* buffer, int size);
void gua_semantic_lint_report_destroy(gua_semantic_lint_report_t* report);
#ifdef __cplusplus
}
#endif
#endif
