#include "gua/runtime.h"
#include <cassert>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <string>
#include <vector>

template<class Copy> std::string json(Copy copy) {
    const int size=copy(nullptr,0); assert(size>0);
    std::vector<char> data(static_cast<size_t>(size));
    assert(copy(data.data(),size)==size); return data.data();
}
static void environment(const char* value) {
#ifdef _WIN32
    assert(_putenv_s("GUA_OBSERVATION_PROFILE",value)==0);
#else
    assert(setenv("GUA_OBSERVATION_PROFILE",value,1)==0);
#endif
}
int main(int argc,char** argv) {
    assert(argc==4);
    const bool remote=std::string(argv[2])=="on", player=std::string(argv[3])=="player";
    const auto info=json(gua_runtime_copy_build_info_json);
    assert(info==json(gua_copy_build_info_json));
    assert(info.find("\"flavor\":\""+std::string(argv[1])+"\"")!=std::string::npos);
    assert(info.find(remote?"\"remoteEnabled\":true":"\"remoteEnabled\":false")!=std::string::npos);
    environment("");
    auto* r=gua_runtime_create(); assert(r);
    assert(gua_runtime_get_observation_profile(r)==(player?GUA_OBSERVATION_PROFILE_PLAYER:GUA_OBSERVATION_PROFILE_DEBUG));
    auto* c=gua_runtime_borrow_context(r); assert(c);
    gua_begin_frame(c,"variant");
    gua_register_node(c,"public","button","Public",{0,0,10,10},1,1);
    gua_register_node(c,"secret","button","Hidden secret",{0,0,10,10},0,1);
    gua_register_node(c,"text","textbox","Public text",{0,0,10,10},1,1);
    gua_end_frame(c);
    const auto tree=json([&](char* out,int size){return gua_copy_ui_tree_json(c,out,size);});
    assert((tree.find("Hidden secret")==std::string::npos)==player);
    assert(tree.find("Public")!=std::string::npos);
    char buffer[8192]{};
    if(player) {
        assert(!gua_runtime_set_observation_profile(r,GUA_OBSERVATION_PROFILE_DEBUG));
        assert(!gua_runtime_create_observe_client(r,GUA_OBSERVATION_PROFILE_DEBUG));
        assert(!gua_runtime_create_spatial_client(r));
        assert(!gua_copy_ui_tree_json_for_profile(c,GUA_OBSERVATION_PROFILE_DEBUG,buffer,sizeof(buffer)));
        gua_observe_result_t* observation=nullptr;
        assert(gua_observe_snapshot(c,GUA_OBSERVATION_PROFILE_DEBUG,&observation)==GUA_OBSERVE_ARGUMENT);
        assert(!observation);
        gua_node_state_v2_t state{sizeof(state)};
        assert(!gua_get_node_state_v2(c,"secret",&state));
        assert(gua_enqueue_click(c,"secret")==0);
        const auto owner=gua_create_game_input_owner(c);
        gua_game_input_request_descriptor_v2_t input{sizeof(input),owner,GUA_GAME_INPUT_KEYBOARD,GUA_GAME_INPUT_DOWN,"KeyA","null",0,0,5000};
        uint64_t id=0;
        assert(gua_enqueue_game_input_v2(c,&input,&id)==GUA_GAME_INPUT_ERROR_UNSUPPORTED);
        gua_runtime_set_game_input_capabilities(r,GUA_RUNTIME_GAME_INPUT_KEYBOARD);
        gua_runtime_set_player_game_input_capabilities(r,GUA_RUNTIME_GAME_INPUT_KEYBOARD);
        assert(gua_enqueue_game_input_v2(c,&input,&id)==GUA_GAME_INPUT_OK);
        // Revocation must be enforced even through a borrowed core context.
        gua_runtime_set_player_game_input_capabilities(r,0);
        gua_game_input_request_v1_t consumed{sizeof(consumed)};
        assert(!gua_consume_game_input_request(c,&consumed));
        gua_runtime_set_player_game_input_capabilities(r,GUA_RUNTIME_GAME_INPUT_KEYBOARD);
        assert(gua_enqueue_game_input_v2(c,&input,&id)==GUA_GAME_INPUT_OK);
        assert(gua_consume_game_input_request(c,&consumed));
        assert(gua_complete_game_input_request(c,consumed.request_id,1,0));
        gua_runtime_set_player_game_input_capabilities(r,0);
        assert(gua_consume_game_input_request(c,&consumed));
        assert(consumed.kind==GUA_GAME_INPUT_CLEANUP && consumed.operation==GUA_GAME_INPUT_RELEASE_ALL);
        assert(gua_complete_game_input_request(c,consumed.request_id,1,0));
        assert(gua_release_game_input_owner(c,owner));
        // Legacy polling must leave nonlegacy correlated completions intact.
        gua_action_request_descriptor_t descriptor{sizeof(descriptor)};
        descriptor.action=GUA_ACTION_SET_VALUE; descriptor.node_id="text"; descriptor.value="updated";
        uint64_t request_id=0;
        assert(gua_enqueue_action(c,&descriptor,&request_id)==GUA_ACTION_ACCEPTED && request_id);
        gua_action_request_t request{sizeof(request)};
        assert(gua_consume_action_request(c,GUA_ACTION_SET_VALUE,"text",&request));
        gua_action_result_t result{sizeof(result),request_id,GUA_ACTION_SET_VALUE,GUA_ACTION_STATUS_SUCCEEDED,0,"text","updated",0};
        assert(gua_emit_action_result(c,&result));
        gua_event_t legacy{}; assert(!gua_poll_event(c,&legacy));
        gua_event_v3_t completion{sizeof(completion)};
        assert(gua_poll_event_v3_for_request(c,request_id,&completion));
        assert(completion.base.request_id==request_id && completion.base.action==GUA_ACTION_SET_VALUE);
        assert(!gua_poll_event_v3_for_request(c,request_id,&completion));
        // Private requestless events cannot alter status, stale reset counts or discarded counts.
        gua_reset_options_t events_reset{sizeof(events_reset),GUA_RESET_EVENTS,0,UINT64_MAX,GUA_RESET_FLAGS_VERSION_CURRENT};
        gua_reset_report_t before{sizeof(before)}, after{sizeof(after)};
        assert(gua_reset_context(c,&events_reset,&before)==GUA_RESET_ERROR_STALE_EPOCH);
        assert(before.unconsumed_event_count==0);
        assert(gua_emit_click(c,"secret"));
        assert(gua_reset_context(c,&events_reset,&after)==GUA_RESET_ERROR_STALE_EPOCH);
        assert(after.unconsumed_event_count==before.unconsumed_event_count && after.discarded_event_count==0);
        gua_context_status_t status{sizeof(status)};
        assert(gua_get_context_status(c,&status) && status.unconsumed_event_count==0);
        assert(gua_emit_click(c,"public"));
        assert(gua_reset_context(c,&events_reset,&after)==GUA_RESET_ERROR_STALE_EPOCH);
        assert(after.unconsumed_event_count==1);
        events_reset.expected_session_epoch=0;
        assert(gua_reset_context(c,&events_reset,&after)==GUA_RESET_SUCCEEDED);
        assert(after.unconsumed_event_count==1 && after.discarded_event_count==1);
    }
    assert(gua_enqueue_click(c,"public"));
    assert(gua_consume_click_request(c,"public"));
    assert(gua_emit_click(c,"public"));
    gua_reset_options_t reset{sizeof(reset),GUA_RESET_DEFAULT_V3,0,0,GUA_RESET_FLAGS_VERSION_CURRENT};
    gua_reset_report_t report{sizeof(report)};
    assert(gua_runtime_reset_context(r,&reset,&report)==GUA_RESET_SUCCEEDED);
    if(player) { assert(!gua_runtime_set_observation_profile(r,0)); assert(gua_runtime_get_observation_profile(r)==1); }
    assert(!gua_runtime_start_inspector_bridge(r,0));
    const int port=19461;
    assert((gua_runtime_start_inspector_bridge(r,port)!=0)==remote);
    assert((gua_runtime_inspector_bridge_running(r)!=0)==remote);
    assert((std::strlen(gua_runtime_inspector_bridge_url(r))!=0)==remote);
    gua_runtime_stop_inspector_bridge(r); gua_runtime_stop_inspector_bridge(r);
    assert(!gua_runtime_inspector_bridge_running(r));
    assert(std::strlen(gua_runtime_inspector_bridge_url(r))==0);
    gua_runtime_destroy(r);
    environment("debug");
    r=gua_runtime_create(); assert((r==nullptr)==player); if(r) gua_runtime_destroy(r);
    environment("player"); r=gua_runtime_create(); assert(r);
    assert(gua_runtime_get_observation_profile(r)==1);
    assert(!gua_runtime_set_observation_profile(r,0));
    if(!remote) assert(!gua_runtime_start_inspector_bridge(r,port));
    gua_runtime_destroy(r); environment("");
    std::cout<<info<<"\nvariant acceptance passed\n";
}
