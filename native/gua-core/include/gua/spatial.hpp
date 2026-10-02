#pragma once
#include "spatial.h"
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>
namespace gua {
class SpatialError : public std::runtime_error {
public:
    int code; std::string path;
    explicit SpatialError(const gua_spatial_error_t& e)
        : std::runtime_error("Spatial contract error " + std::to_string(e.code) + " at " + e.path), code(e.code), path(e.path) {}
};
class SpatialDocument {
    gua_spatial_document_t* value_ = nullptr;
    explicit SpatialDocument(gua_spatial_document_t* value):value_(value) {}
    static void check(int status, const gua_spatial_error_t& error) { if(status) throw SpatialError(error); }
public:
    static SpatialDocument adopt(gua_spatial_document_t* value) {
        if(!value) throw std::invalid_argument("Null spatial document");
        return SpatialDocument(value);
    }
    SpatialDocument(int type, const std::string& json) {
        if(json.size()>UINT32_MAX) throw std::length_error("Spatial document too large");
        gua_spatial_parse_options_v1_t options{sizeof(options),type}; gua_spatial_error_t error{};
        check(gua_spatial_from_json(&options,{json.data(),static_cast<uint32_t>(json.size())},&value_,&error),error);
    }
    ~SpatialDocument() { gua_spatial_destroy(value_); }
    SpatialDocument(const SpatialDocument&)=delete;
    SpatialDocument& operator=(const SpatialDocument&)=delete;
    SpatialDocument(SpatialDocument&& other) noexcept : value_(std::exchange(other.value_,nullptr)) {}
    SpatialDocument& operator=(SpatialDocument&& other) noexcept {
        if(this!=&other) { gua_spatial_destroy(value_); value_=std::exchange(other.value_,nullptr); } return *this;
    }
    const gua_spatial_document_t* get() const { return value_; }
    int type() const { return gua_spatial_document_type(value_); }
    std::string to_json() const {
        const int n=gua_spatial_copy_json(value_,nullptr,0); if(n<=0) throw std::runtime_error("Spatial copy failed");
        std::vector<char> buffer(static_cast<size_t>(n));
        if(gua_spatial_copy_json(value_,buffer.data(),n)!=n) throw std::runtime_error("Spatial copy failed");
        return {buffer.data(),static_cast<size_t>(n-1)};
    }
    void check_request(const SpatialDocument& provider) const {
        gua_spatial_error_t error{}; check(gua_spatial_check_request(value_,provider.value_,&error),error);
    }
    void check_result(const SpatialDocument& result) const {
        gua_spatial_error_t error{}; check(gua_spatial_check_result(value_,result.value_,&error),error);
    }
};
}
