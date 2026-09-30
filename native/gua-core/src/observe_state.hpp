#pragma once
#include "gua/observe.h"
#include <map>
#include <climits>
#include <random>
struct ObserveSample {
    std::shared_ptr<gua_value_t> value;
    int error = GUA_OBSERVE_NOT_SAMPLED;
};
struct ObserveOwner { uint64_t id; int source; std::string runtime_id; };
struct ObserveRegistration {
    uint64_t id, owner;
    std::string name;
    bool player, sensitive;
    ObserveSample current;
    std::optional<ObserveSample> staged;
};
struct ObserveJournal {
    uint64_t sequence = 0, revision = 0, generation = 0;
    size_t bytes = 0;
    std::deque<std::pair<uint64_t, std::string>> events;
    std::map<uint64_t, std::string> catalogs;
};
struct ObserveSubscription { int profile; uint64_t epoch, cursor, generation; };
struct ObserveState {
    std::string source_id;
    uint64_t next_id = 1;
    uint32_t max_events = 1024;
    uint64_t max_bytes = 8 * 1024 * 1024;
    std::map<uint64_t, ObserveOwner> owners;
    std::map<uint64_t, ObserveRegistration> registrations;
    std::map<uint64_t, ObserveSubscription> subscriptions;
    std::unordered_set<uint64_t> player_owners;
    ObserveJournal journals[2];
    ObserveState() {
        std::random_device random;
        std::ostringstream id;
        id << std::hex;
        for (int i = 0; i < 4; ++i) id << std::setw(8) << std::setfill('0') << random();
        source_id = id.str();
    }
};
struct gua_observe_result_t { std::string json; std::string catalogs = "[]"; };
