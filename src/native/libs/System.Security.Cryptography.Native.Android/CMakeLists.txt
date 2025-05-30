project(System.Security.Cryptography.Native.Android C)

add_compile_options(-Wno-gnu-zero-variadic-macro-arguments)
add_compile_options(-Wno-unused-parameter)
add_compile_options(-Wno-unused-function)
add_compile_options(-Wnonnull)

set(NATIVECRYPTO_SOURCES
    pal_bignum.c
    pal_dsa.c
    pal_ecdsa.c
    pal_ecc_import_export.c
    pal_eckey.c
    pal_ecdh.c
    pal_err.c
    pal_evp.c
    pal_cipher.c
    pal_hmac.c
    pal_jni.c
    pal_lifetime.c
    pal_memory.c
    pal_misc.c
    pal_pbkdf2.c
    pal_rsa.c
    pal_signature.c
    pal_ssl.c
    pal_sslstream.c
    pal_trust_manager.c
    pal_x509.c
    pal_x509chain.c
    pal_x509store.c
)

add_library(System.Security.Cryptography.Native.Android
    SHARED
    ${NATIVECRYPTO_SOURCES} pal_jni_onload.c
    ${VERSION_FILE_PATH}
)

add_library(System.Security.Cryptography.Native.Android-Static
    STATIC
    ${NATIVECRYPTO_SOURCES}
)

set_target_properties(System.Security.Cryptography.Native.Android-Static PROPERTIES OUTPUT_NAME System.Security.Cryptography.Native.Android CLEAN_DIRECT_OUTPUT 1)

target_link_libraries(System.Security.Cryptography.Native.Android
    PRIVATE
    -llog
)

set_target_properties(System.Security.Cryptography.Native.Android PROPERTIES OUTPUT_NAME "System.Security.Cryptography.Native.Android")
set_target_properties(System.Security.Cryptography.Native.Android-Static PROPERTIES OUTPUT_NAME "System.Security.Cryptography.Native.Android")

if (GEN_SHARED_LIB)
    install_with_stripped_symbols (System.Security.Cryptography.Native.Android PROGRAMS .)
endif()

install (TARGETS System.Security.Cryptography.Native.Android-Static DESTINATION ${STATIC_LIB_DESTINATION} COMPONENT libs)
