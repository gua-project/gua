#include <gua/runtime.h>
#include <atomic>
#include <thread>
#include <vector>
#include <cstring>
#include <cstdio>
extern "C" unsigned gua_game_guard_initializations();
extern "C" __declspec(dllimport) int unrelated_value();
extern "C" __declspec(dllimport) int plugin_value();
#ifdef LEGACY_HOOK
int unrelated_delay_notifications();
#endif
// A user global constructor may call Gua: the game bootstrap must precede it.
static int global_ok=[](){char text[8192];return gua_copy_build_info_json(text,sizeof(text))>0;}();
int main(){
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
  bridge=gua_runtime_start_inspector_bridge(rt,19473);
  bool remote=bridge!=0 && gua_runtime_inspector_bridge_running(rt)!=0;
#else
  // This is a negative probe, not a production remote-start path.
  bridge=gua_runtime_start_inspector_bridge(rt,19473);
  bool remote=bridge==0 && gua_runtime_inspector_bridge_running(rt)==0;
#endif
  gua_runtime_stop_inspector_bridge(rt);gua_runtime_destroy(rt);
  bool ok=global_ok && successes==16 && unrelated_value()==42 && plugin_value()>0 && local && profile && remote;
#ifdef LEGACY_HOOK
  ok=ok && unrelated_delay_notifications()>0;
#endif
  std::printf("mode=%s initializationCount=%u threads=%d local=%d profile=%d bridge=%d\n",EXPECT_MODE,gua_game_guard_initializations(),successes.load(),local,profile,bridge);
  return ok?0:11;
}
