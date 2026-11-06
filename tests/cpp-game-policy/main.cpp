#include <gua/runtime.h>
#include <atomic>
#include <thread>
#include <vector>
#include <cstring>
#include <cstdio>
#include <chrono>
extern "C" unsigned gua_game_guard_initializations();
extern "C" __declspec(dllimport) int unrelated_value();
extern "C" __declspec(dllimport) int plugin_value();
#ifdef LEGACY_HOOK
int unrelated_delay_notifications();
#endif
// A user global constructor may call Gua: the game bootstrap must precede it.
static int global_ok=[](){char text[8192];return gua_copy_build_info_json(text,sizeof(text))>0;}();
int main(int argc,char** argv){
  bool wire=argc==3 && std::strcmp(argv[1],"--wire")==0;
  std::atomic<int> successes{};std::vector<std::thread> threads;
  for(int i=0;i<16;++i) threads.emplace_back([&]{
    auto* ctx=gua_create_context();if(ctx){gua_destroy_context(ctx);if(gua_game_guard_initializations()==1)++successes;}
  });
  for(auto& thread:threads)thread.join();
  auto* rt=gua_runtime_create();if(!rt)return 10;
  gua_runtime_begin_frame(rt,"local");gua_runtime_register_node(rt,"button","button","Local",{0,0,10,10},1,1);gua_runtime_end_frame(rt);
  char tree[8192];bool local=gua_runtime_copy_ui_tree_json(rt,tree,sizeof(tree))>0 && std::strstr(tree,"Local");
  bool player=std::strcmp(EXPECT_MODE,"player")==0;
  bool profile=!player || (gua_runtime_get_observation_profile(rt)==GUA_OBSERVATION_PROFILE_PLAYER && gua_runtime_set_observation_profile(rt,GUA_OBSERVATION_PROFILE_DEBUG)==0);
  int bridge=0;
#ifdef GUA_REMOTE_AUTOMATION
  gua_runtime_set_virtual_clock_enabled(rt,1);
  bridge=gua_runtime_start_inspector_bridge(rt,wire?19573:19473);
  bool remote=bridge!=0 && gua_runtime_inspector_bridge_running(rt)!=0;
#else
  // This is a negative probe, not a production remote-start path.
  bridge=gua_runtime_start_inspector_bridge(rt,19473);
  bool remote=bridge==0 && gua_runtime_inspector_bridge_running(rt)==0;
#endif
  bool consumed=!wire;
  if(wire && remote) {
    if(auto* report=std::fopen(argv[2],"wb")){std::fputs("{\"ready\":true,\"consumed\":0}",report);std::fclose(report);}else return 12;
    int clicks=0;auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(15);
    while(std::chrono::steady_clock::now()<deadline) {
      gua_action_request_t request{};request.struct_size=sizeof(request);
      while(gua_runtime_consume_action_request(rt,GUA_ACTION_CLICK,"button",&request)>0) {
        ++clicks;gua_action_result_t result{};result.struct_size=sizeof(result);result.request_id=request.request_id;
        result.action=GUA_ACTION_CLICK;result.status=GUA_ACTION_STATUS_SUCCEEDED;result.node_id="button";
        if(!gua_runtime_emit_action_result(rt,&result))return 13;
      }
      if(clicks){std::this_thread::sleep_for(std::chrono::milliseconds(100));break;}
      std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    consumed=clicks==1;
    if(auto* report=std::fopen(argv[2],"wb")){std::fprintf(report,"{\"ready\":true,\"consumed\":%d}",clicks);std::fclose(report);}else return 12;
  }
  gua_runtime_stop_inspector_bridge(rt);gua_runtime_destroy(rt);
  bool ok=global_ok && successes==16 && unrelated_value()==42 && plugin_value()>0 && local && profile && remote && consumed;
#ifdef LEGACY_HOOK
  ok=ok && unrelated_delay_notifications()>0;
#endif
  std::printf("mode=%s initializationCount=%u threads=%d local=%d profile=%d bridge=%d\n",EXPECT_MODE,gua_game_guard_initializations(),successes.load(),local,profile,bridge);
  return ok?0:11;
}
