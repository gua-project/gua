#include <windows.h>
#include <delayimp.h>
static LONG notifications=0;
FARPROC WINAPI test_notify(unsigned,PDelayLoadInfo){InterlockedIncrement(&notifications);return nullptr;}
FARPROC WINAPI test_failure(unsigned,PDelayLoadInfo){return nullptr;}
int unrelated_delay_notifications(){return static_cast<int>(notifications);}
