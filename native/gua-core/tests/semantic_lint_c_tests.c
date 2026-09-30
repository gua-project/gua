#include "gua/semantic_lint.h"
#include <assert.h>
#include <stdlib.h>
#include <string.h>
int main(void) {
    gua_context_t* ctx = gua_create_context();
    gua_semantic_lint_report_t* report = NULL;
    gua_semantic_lint_options_v1_t options = {sizeof(options), GUA_OBSERVATION_PROFILE_DEBUG, 0};
    assert(gua_semantic_lint_analyze(ctx, &options, &report) == 0);
    int size = gua_semantic_lint_report_copy_json(report, NULL, 0);
    assert(size > 1);
    char sentinel[1] = {'x'};
    assert(gua_semantic_lint_report_copy_json(report, sentinel, 1) == size && sentinel[0] == 'x');
    char* json = (char*)malloc((size_t)size);
    assert(gua_semantic_lint_report_copy_json(report, json, size) == size);
    assert(json[size-1] == '\0' && strstr(json, "\"worldObjectTree\":null"));
    gua_destroy_context(ctx);
    assert(gua_semantic_lint_report_copy_json(report, json, size) == size);
    gua_semantic_lint_report_destroy(report); free(json);
    gua_semantic_lint_report_destroy(NULL);
    return 0;
}
