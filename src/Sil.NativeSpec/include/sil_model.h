/*
 * sil_model.h — SIL Runtime native model ABI, version 1.
 *
 * Normative description: spec/native-abi.md (FROZEN).
 * This header is the canonical C declaration of that contract. Do not change v1 behaviour;
 * add a new SIL_ABI_VERSION instead.
 *
 * A model library exports the eight entry points below with C linkage and the platform's
 * default C calling convention. Strings are NUL-terminated UTF-8. Models must be
 * deterministic: no randomness, no wall-clock, no threads, no I/O.
 */

#ifndef SIL_MODEL_H
#define SIL_MODEL_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define SIL_ABI_VERSION 1

/* status codes */
#define SIL_OK              0
#define SIL_ERR_ALLOC       1
#define SIL_ERR_INVALID_ARG 2
#define SIL_ERR_RANGE       3
#define SIL_ERR_STATE       4

/* port directions */
#define SIL_PORT_INPUT  0
#define SIL_PORT_OUTPUT 1

/* fixed string buffer sizes, including the NUL terminator */
#define SIL_NAME_MAX 64
#define SIL_UNIT_MAX 32

#if defined(_WIN32)
#  define SIL_EXPORT __declspec(dllexport)
#else
#  define SIL_EXPORT __attribute__((visibility("default")))
#endif

typedef struct sil_port_info {
    int32_t index;                  /* 0-based; must equal the port's position */
    int32_t direction;              /* SIL_PORT_INPUT | SIL_PORT_OUTPUT */
    char    name[SIL_NAME_MAX];     /* NUL-terminated UTF-8, never empty */
    char    unit[SIL_UNIT_MAX];     /* NUL-terminated UTF-8, empty when dimensionless */
} sil_port_info_t;

/* Returns SIL_ABI_VERSION. Callable without an instance; the loader calls it first. */
SIL_EXPORT int32_t sil_abi_version(void);

/* Allocates an instance in its t=0 state with outputs published. Multiple instances
 * from one library must be independent: no global mutable state. */
SIL_EXPORT int32_t sil_init(void** instance);

/* Advances one fixed step of dt seconds and republishes outputs. dt is finite and > 0. */
SIL_EXPORT int32_t sil_step(void* instance, double dt);

/* Number of ports. Constant for the lifetime of the instance. */
SIL_EXPORT int32_t sil_port_count(void* instance, int32_t* count);

/* Fills the declaration of port `index`. Out of range yields SIL_ERR_RANGE. */
SIL_EXPORT int32_t sil_port_info(void* instance, int32_t index, sil_port_info_t* info);

/* Reads a port value. Both directions are readable. */
SIL_EXPORT int32_t sil_get(void* instance, int32_t index, double* value);

/* Writes a port value. Writing an output is allowed but the next step overwrites it. */
SIL_EXPORT int32_t sil_set(void* instance, int32_t index, double value);

/* Releases an instance. NULL is a no-op. The handle must not be used afterwards. */
SIL_EXPORT void sil_free(void* instance);

#ifdef __cplusplus
}
#endif

#endif /* SIL_MODEL_H */
