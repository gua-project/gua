#pragma once
#include "gua.hpp"
#include "semantic_lint.h"
namespace gua {
struct SemanticLintOptions {
    int observation_profile = GUA_OBSERVATION_PROFILE_DEBUG;
    bool include_world = true;
};
class SemanticLintReport {
    std::unique_ptr<gua_semantic_lint_report_t, decltype(&gua_semantic_lint_report_destroy)> report_;
public:
    explicit SemanticLintReport(gua_semantic_lint_report_t* report) : report_(report, gua_semantic_lint_report_destroy) {}
    [[nodiscard]] std::string json() const {
        const int size = gua_semantic_lint_report_copy_json(report_.get(), nullptr, 0);
        if (size <= 0) throw std::runtime_error("Invalid semantic lint report");
        std::string result(static_cast<size_t>(size), '\0');
        gua_semantic_lint_report_copy_json(report_.get(), result.data(), size);
        result.pop_back(); return result;
    }
};
class SemanticLinter {
public:
    [[nodiscard]] static SemanticLintReport analyze(Context& context, SemanticLintOptions options = {}) {
        gua_semantic_lint_options_v1_t native {sizeof(native), options.observation_profile, options.include_world ? 1 : 0};
        gua_semantic_lint_report_t* report = nullptr;
        if (gua_semantic_lint_analyze(context.native_handle(), &native, &report)) throw std::runtime_error("Semantic lint failed");
        return SemanticLintReport(report);
    }
    [[nodiscard]] static SemanticLintReport analyze_snapshots(const std::string& ui, const char* world = nullptr,
        int profile = GUA_OBSERVATION_PROFILE_DEBUG) {
        gua_semantic_lint_report_t* report = nullptr;
        if (gua_semantic_lint_analyze_snapshots(ui.c_str(), world, profile, &report)) throw std::invalid_argument("Invalid published snapshot");
        return SemanticLintReport(report);
    }
};
}
