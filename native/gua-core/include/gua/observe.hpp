#pragma once
#include "gua.hpp"
#include "value.hpp"
#include "observe.h"
#include <functional>
namespace gua {
class ObserveError : public std::runtime_error {
public:
    int code;
    explicit ObserveError(int c) : std::runtime_error("Observe error " + std::to_string(c)), code(c) {}
};
inline void observe_check(int code) { if (code) throw ObserveError(code); }
inline std::string observe_result(gua_observe_result_t* result, bool transport=false) {
    std::unique_ptr<gua_observe_result_t,decltype(&gua_observe_result_destroy)> owned(result,gua_observe_result_destroy);
    return value_copy_json(result,transport ? gua_observe_result_copy_transport_json : gua_observe_result_copy_json);
}
struct ObserveGetter {
    std::weak_ptr<ContextLifetime> context;
    uint64_t id = 0;
    int source = 0;
    std::function<Value()> getter;
    std::weak_ptr<std::function<bool(bool)>> observer;
    void detach() {
        if (auto c=context.lock()) if (auto callback=observer.lock()) c->frame_observers.remove(callback);
        observer.reset();
    }
    gua_context_t* handle() const {
        auto c=context.lock(); return c && c->context ? c->context->native_handle() : nullptr;
    }
    void sample(bool stage) {
        if (!id || !handle()) return;
        int alive=gua_observe_registration_alive(handle(),id);
        if(alive==GUA_OBSERVE_STALE) { id=0; getter={}; detach(); if(!stage) observe_check(alive); return; }
        observe_check(alive);
        std::optional<Value> value; int error=0;
        try { auto evaluate=getter; value.emplace(evaluate()); }
        catch (const ValueError& e) { error=e.code; }
        catch (...) { error=GUA_OBSERVE_GETTER_FAILED; }
        auto c=handle(); if(!c || !id) return;
        int status=gua_observe_publish(c,id,value ? value->get() : nullptr,error,stage ? 1 : 0);
        if(status==GUA_OBSERVE_STALE) { id=0; getter={}; detach(); return; }
        observe_check(status);
    }
    ~ObserveGetter() { detach(); if(id && handle()) gua_observe_unregister(handle(),id); }
};
class ObserveRegistration {
    std::shared_ptr<ObserveGetter> state_;
public:
    explicit ObserveRegistration(std::shared_ptr<ObserveGetter> state) : state_(std::move(state)) {}
    ObserveRegistration(ObserveRegistration&&)=default;
    ObserveRegistration& operator=(ObserveRegistration&&)=default;
    ObserveRegistration(const ObserveRegistration&)=delete;
    void notify() { auto state=state_; if(!state || !state->id || !state->handle()) throw ObserveError(GUA_OBSERVE_STALE); state->sample(false); }
    void reset() { if(state_) { if(state_->id && state_->handle()) gua_observe_unregister(state_->handle(),state_->id); state_->id=0; state_->getter={}; state_->detach(); state_.reset(); } }
    ~ObserveRegistration() { reset(); }
};
class ObserveOwner {
    std::weak_ptr<ContextLifetime> context_;
    uint64_t id_=0;
    int source_=0;
    gua_context_t* handle() const { auto c=context_.lock(); return c && c->context ? c->context->native_handle() : nullptr; }
public:
    ObserveOwner(Context& context,int source,std::string_view runtime_id={}) : context_(context.lifetime()),source_(source) {
        observe_check(gua_observe_create_owner(context.native_handle(),source,value_text(runtime_id),&id_));
    }
    ObserveOwner(const ObserveOwner&)=delete;
    ObserveOwner& operator=(const ObserveOwner&)=delete;
    ObserveOwner(ObserveOwner&& other) noexcept : context_(std::move(other.context_)),id_(std::exchange(other.id_,0)),source_(other.source_) {}
    ObserveOwner& operator=(ObserveOwner&& other) noexcept { if(this!=&other) { reset(); context_=std::move(other.context_); id_=std::exchange(other.id_,0); source_=other.source_; } return *this; }
    ~ObserveOwner() { reset(); }
    void reset() { if(id_ && handle()) gua_observe_destroy_owner(handle(),id_); id_=0; }
    ObserveRegistration property(std::string_view name,std::function<Value()> getter,bool player=false,bool sensitive=false) {
        if(source_!=GUA_OBSERVE_WORLD) throw ObserveError(GUA_OBSERVE_ARGUMENT);
        return observe(name,std::move(getter),player,sensitive);
    }
    ObserveRegistration observe(std::string_view name,std::function<Value()> getter,bool player=false,bool sensitive=false) {
        if(!handle() || !id_ || !getter) throw ObserveError(GUA_OBSERVE_STALE);
        auto state=std::make_shared<ObserveGetter>(); state->context=context_; state->source=source_; state->getter=std::move(getter);
        gua_observe_registration_v1_t d{sizeof(d),id_,value_text(name),player?1:0,sensitive?1:0};
        observe_check(gua_observe_register_v1(handle(),&d,&state->id));
        auto lifetime=context_.lock(); std::weak_ptr<ObserveGetter> weak=state;
        auto callback=std::make_shared<std::function<bool(bool)>>([weak](bool ui) {
            auto entry=weak.lock(); if(!entry || !entry->id) return false;
            if((entry->source==GUA_OBSERVE_UI)==ui) entry->sample(true);
            return entry->id!=0;
        });
        state->observer=callback;
        lifetime->frame_observers.push_back(std::move(callback));
        return ObserveRegistration(std::move(state));
    }
};
class ObserveSubscription {
    std::weak_ptr<ContextLifetime> context_;
    uint64_t id_=0;
    gua_context_t* handle() const { auto c=context_.lock(); return c && c->context ? c->context->native_handle() : nullptr; }
public:
    std::string snapshot;
    std::string snapshot_transport;
    explicit ObserveSubscription(Context& c,int profile=GUA_OBSERVATION_PROFILE_DEBUG) : context_(c.lifetime()) {
        gua_observe_result_t* result=nullptr;
        observe_check(gua_observe_subscribe(c.native_handle(),profile,&id_,&result));
        try {
            std::unique_ptr<gua_observe_result_t,decltype(&gua_observe_result_destroy)> owned(result,gua_observe_result_destroy);
            snapshot=value_copy_json(result,gua_observe_result_copy_json);
            snapshot_transport=value_copy_json(result,gua_observe_result_copy_transport_json);
        } catch(...) { gua_observe_unsubscribe(c.native_handle(),id_); throw; }
    }
    ObserveSubscription(const ObserveSubscription&)=delete;
    ObserveSubscription& operator=(const ObserveSubscription&)=delete;
    ~ObserveSubscription() { if(id_ && handle()) gua_observe_unsubscribe(handle(),id_); }
    std::string poll() { if(!handle()) throw ObserveError(GUA_OBSERVE_STALE); gua_observe_result_t* r=nullptr; observe_check(gua_observe_poll(handle(),id_,&r)); return observe_result(r); }
    std::string poll_transport() { if(!handle()) throw ObserveError(GUA_OBSERVE_STALE); gua_observe_result_t* r=nullptr; observe_check(gua_observe_poll(handle(),id_,&r)); return observe_result(r,true); }
};
inline std::string observe_snapshot(Context& c,int profile=GUA_OBSERVATION_PROFILE_DEBUG) {
    gua_observe_result_t* r=nullptr; observe_check(gua_observe_snapshot(c.native_handle(),profile,&r)); return observe_result(r);
}
inline std::string observe_snapshot_transport(Context& c,int profile=GUA_OBSERVATION_PROFILE_DEBUG) {
    gua_observe_result_t* r=nullptr; observe_check(gua_observe_snapshot(c.native_handle(),profile,&r)); return observe_result(r,true);
}
}
