/*
 * sil_first_order.c — reference native model for the SIL ABI v1.
 *
 * First-order lag integrated with classical RK4:
 *
 *     dx/dt = (K*u - x) / tau,   tau = 1.0, K = 1.0, x(0) = 1.0
 *
 * With u held at 0 this reduces to dx/dt = -x from x0 = 1, which is exactly the frozen
 * decay vector in DESIGN.md. The arithmetic below is written in the same operation order as
 * the managed Rk4Integrator so that the two agree bit-for-bit, not merely to a tolerance.
 *
 * Ports:  0  u  input   (command)
 *         1  x  output  (state)
 *
 * Build (see spec/native-abi.md section 8):
 *     cc -O2 -std=c11 -ffp-contract=off -shared -fPIC -Iinclude src/sil_first_order.c \
 *        -o libsil_first_order.dylib
 */

#include <stdlib.h>
#include <string.h>

#include "sil_model.h"

#define PORT_U 0
#define PORT_X 1
#define PORT_COUNT 2

static const double TAU = 1.0;
static const double GAIN = 1.0;
static const double X0 = 1.0;

static const sil_port_info_t PORTS[PORT_COUNT] = {
    { PORT_U, SIL_PORT_INPUT,  "u", "" },
    { PORT_X, SIL_PORT_OUTPUT, "x", "" },
};

typedef struct {
    double u;
    double x;
} instance_t;

static double derivative(const instance_t* s, double x)
{
    return ((GAIN * s->u) - x) / TAU;
}

int32_t sil_abi_version(void)
{
    return SIL_ABI_VERSION;
}

int32_t sil_init(void** instance)
{
    instance_t* s;

    if (instance == NULL) {
        return SIL_ERR_INVALID_ARG;
    }

    s = (instance_t*)calloc(1, sizeof(instance_t));
    if (s == NULL) {
        return SIL_ERR_ALLOC;
    }

    s->u = 0.0;
    s->x = X0;

    *instance = s;
    return SIL_OK;
}

int32_t sil_step(void* instance, double dt)
{
    instance_t* s = (instance_t*)instance;
    double half, k1, k2, k3, k4;

    if (s == NULL) {
        return SIL_ERR_INVALID_ARG;
    }

    if (!(dt > 0.0) || dt != dt) {
        return SIL_ERR_INVALID_ARG;
    }

    half = dt * 0.5;
    k1 = derivative(s, s->x);
    k2 = derivative(s, s->x + (half * k1));
    k3 = derivative(s, s->x + (half * k2));
    k4 = derivative(s, s->x + (dt * k3));

    s->x += (dt / 6.0) * (k1 + (2.0 * k2) + (2.0 * k3) + k4);
    return SIL_OK;
}

int32_t sil_port_count(void* instance, int32_t* count)
{
    if (instance == NULL || count == NULL) {
        return SIL_ERR_INVALID_ARG;
    }

    *count = PORT_COUNT;
    return SIL_OK;
}

int32_t sil_port_info(void* instance, int32_t index, sil_port_info_t* info)
{
    if (instance == NULL || info == NULL) {
        return SIL_ERR_INVALID_ARG;
    }

    if (index < 0 || index >= PORT_COUNT) {
        return SIL_ERR_RANGE;
    }

    memcpy(info, &PORTS[index], sizeof(sil_port_info_t));
    return SIL_OK;
}

int32_t sil_get(void* instance, int32_t index, double* value)
{
    instance_t* s = (instance_t*)instance;

    if (s == NULL || value == NULL) {
        return SIL_ERR_INVALID_ARG;
    }

    switch (index) {
    case PORT_U: *value = s->u; return SIL_OK;
    case PORT_X: *value = s->x; return SIL_OK;
    default:     return SIL_ERR_RANGE;
    }
}

int32_t sil_set(void* instance, int32_t index, double value)
{
    instance_t* s = (instance_t*)instance;

    if (s == NULL) {
        return SIL_ERR_INVALID_ARG;
    }

    switch (index) {
    case PORT_U: s->u = value; return SIL_OK;
    case PORT_X: s->x = value; return SIL_OK;
    default:     return SIL_ERR_RANGE;
    }
}

void sil_free(void* instance)
{
    free(instance);
}
