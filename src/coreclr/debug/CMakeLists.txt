
add_compile_definitions($<${FEATURE_CORECLR_CACHED_INTERFACE_DISPATCH}:FEATURE_CACHED_INTERFACE_DISPATCH>)
add_compile_definitions($<${FEATURE_CORECLR_VIRTUAL_STUB_DISPATCH}:FEATURE_VIRTUAL_STUB_DISPATCH>)

add_subdirectory(daccess)
add_subdirectory(ee)
add_subdirectory(di)
if(CLR_CMAKE_HOST_WIN32)
    add_subdirectory(createdump)
endif(CLR_CMAKE_HOST_WIN32)
if(FEATURE_SINGLE_FILE_DIAGNOSTICS)
    add_subdirectory(runtimeinfo)
endif(FEATURE_SINGLE_FILE_DIAGNOSTICS)
