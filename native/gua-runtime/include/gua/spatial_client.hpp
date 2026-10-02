#pragma once
#include "runtime.h"
#include "gua/spatial.hpp"
#include <optional>
namespace gua {
// Thin owner-scoped C++ read client. Runtime must outlive this connection token.
class SpatialClient {
    gua_runtime_t* runtime_; uint64_t client_;
    std::optional<SpatialDocument> command(int operation,const SpatialDocument* batch=nullptr,uint64_t id=0) {
        gua_spatial_document_t* raw=nullptr;
        int status=gua_runtime_spatial_command(runtime_,client_,operation,batch ? batch->get() : nullptr,id,&raw);
        if(status==GUA_SPATIAL_NOT_READY) return std::nullopt;
        if(status) { gua_spatial_error_t e{}; e.code=status; throw SpatialError(e); }
        if(!raw) return std::nullopt;
        return SpatialDocument::adopt(raw);
    }
public:
    explicit SpatialClient(gua_runtime_t* runtime):runtime_(runtime),client_(gua_runtime_create_spatial_client(runtime)) {
        if(!client_) throw std::runtime_error("Spatial client unavailable");
    }
    ~SpatialClient() { gua_runtime_release_spatial_client(runtime_,client_); }
    SpatialClient(const SpatialClient&)=delete;
    SpatialClient& operator=(const SpatialClient&)=delete;
    SpatialDocument describe() { return std::move(*command(GUA_SPATIAL_INFO)); }
    void enqueue(const SpatialDocument& batch) { command(GUA_SPATIAL_ENQUEUE,&batch); }
    std::optional<SpatialDocument> poll(uint64_t batch_id) { return command(GUA_SPATIAL_POLL,nullptr,batch_id); }
    void cancel(uint64_t batch_id) { command(GUA_SPATIAL_CANCEL,nullptr,batch_id); }
};
}
