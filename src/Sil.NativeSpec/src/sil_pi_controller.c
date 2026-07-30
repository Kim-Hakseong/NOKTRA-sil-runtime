/*
 * sil_pi_controller.c — reference "compiled control code" for the SIL ABI v1.
 *
 * A discrete PI controller with conditional-integration anti-windup. This is the case the
 * product exists for: the plant is a model, the controller is the same C the target actually
 * compiles, and the runtime steps both on one fixed cycle.
 *
 *     e      = setpoint - measurement
 *     I(k)   = I(k-1) + Ki * e * dt        (held when the output is saturated into the error)
 *     u      = Kp * e + I(k),  clamped to [U_MIN, U_MAX]
 *
 * Gains are compile-time constants: ABI v1 declares no parameter entry point
 * (spec/native-abi.md section 9).
 *
 * Ports:  0  setpoint     input
 *         1  measurement  input
 *         2  u            output  (control command)
 *         3  integral     output  (integrator state, for observability)
 *
 * Build (see spec/native-abi.md section 8):
 *     cc -O2 -std=c11 -ffp-contract=off -shared -fPIC -Iinclude src/sil_pi_controller.c \
 *        -o libsil_pi_controller.dylib
 */

#include <stdlib.h>
#include <string.h>

#include "sil_model.h"

#define PORT_SETPOINT    0
#define PORT_MEASUREMENT 1
#define PORT_U           2
#define PORT_INTEGRAL    3
#define PORT_COUNT       4

static const double KP = 2.0;
static const double KI = 5.0;
static const double U_MIN = -50.0;
static const double U_MAX = 50.0;

static const sil_port_info_t PORTS[PORT_COUNT] = {
    { PORT_SETPOINT,    SIL_PORT_INPUT,  "setpoint",    "" },
    { PORT_MEASUREMENT, SIL_PORT_INPUT,  "measurement", "" },
    { PORT_U,           SIL_PORT_OUTPUT, "u",           "" },
    { PORT_INTEGRAL,    SIL_PORT_OUTPUT, "integral",    "" },
};

typedef struct {
    double setpoint;
    double measurement;
    double u;
    double integral;
} instance_t;

static const sil_port_info_t* port_at(int32_t index)
{
    if (index < 0 || index >= PORT_COUNT) {
        return NULL;
    }

    return &PORTS[index];
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

    /* calloc already gives the t=0 state: every port and the integrator are zero. */
    *instance = s;
    return SIL_OK;
}

int32_t sil_step(void* instance, double dt)
{
    instance_t* s = (instance_t*)instance;
    double error, candidate, command;

    if (s == NULL) {
        return SIL_ERR_INVALID_ARG;
    }

    if (!(dt > 0.0) || dt != dt) {
        return SIL_ERR_INVALID_ARG;
    }

    error = s->setpoint - s->measurement;
    candidate = s->integral + (KI * error * dt);
    command = (KP * error) + candidate;

    if (command > U_MAX) {
        command = U_MAX;
        if (error > 0.0) {
            candidate = s->integral;   /* do not wind up further into the limit */
        }
    } else if (command < U_MIN) {
        command = U_MIN;
        if (error < 0.0) {
            candidate = s->integral;
        }
    }

    s->integral = candidate;
    s->u = command;
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
    const sil_port_info_t* port;

    if (instance == NULL || info == NULL) {
        return SIL_ERR_INVALID_ARG;
    }

    port = port_at(index);
    if (port == NULL) {
        return SIL_ERR_RANGE;
    }

    memcpy(info, port, sizeof(sil_port_info_t));
    return SIL_OK;
}

int32_t sil_get(void* instance, int32_t index, double* value)
{
    instance_t* s = (instance_t*)instance;

    if (s == NULL || value == NULL) {
        return SIL_ERR_INVALID_ARG;
    }

    switch (index) {
    case PORT_SETPOINT:    *value = s->setpoint;    return SIL_OK;
    case PORT_MEASUREMENT: *value = s->measurement; return SIL_OK;
    case PORT_U:           *value = s->u;           return SIL_OK;
    case PORT_INTEGRAL:    *value = s->integral;    return SIL_OK;
    default:               return SIL_ERR_RANGE;
    }
}

int32_t sil_set(void* instance, int32_t index, double value)
{
    instance_t* s = (instance_t*)instance;

    if (s == NULL) {
        return SIL_ERR_INVALID_ARG;
    }

    switch (index) {
    case PORT_SETPOINT:    s->setpoint = value;    return SIL_OK;
    case PORT_MEASUREMENT: s->measurement = value; return SIL_OK;
    case PORT_U:           s->u = value;           return SIL_OK;
    case PORT_INTEGRAL:    s->integral = value;    return SIL_OK;
    default:               return SIL_ERR_RANGE;
    }
}

void sil_free(void* instance)
{
    free(instance);
}
