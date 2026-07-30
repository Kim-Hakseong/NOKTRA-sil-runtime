# native-abi.md — SIL C 모델 규약 (v1)

**Status: FROZEN.** Ralph가 M5에서 확정 기재했다. 이후 변경 금지 — 확장이 필요하면
`SIL_ABI_VERSION`을 올린 새 버전을 추가하고 v1 동작은 그대로 둔다.

정본 헤더: `src/Sil.NativeSpec/include/sil_model.h`
참조 구현: `src/Sil.NativeSpec/src/sil_first_order.c`, `src/Sil.NativeSpec/src/sil_pi_controller.c`
로더: `src/Sil.Core/Native/`

---

## 1. Scope

이 규약은 **컴파일된 제어 코드/모델**(C 또는 C 호환 ABI로 빌드된 공유 라이브러리)을 SIL Runtime의
고정 주기 루프에서 실행하기 위한 최소 인터페이스다. 관리 측 `IModel`과 같은 모양이며, 번역 계층이 아니다.

공유 라이브러리 확장자: Windows `.dll` · Linux `.so` · macOS `.dylib`.

## 2. Calling convention & types

- 호출 규약은 **플랫폼 기본 C 규약**이다. x86(32-bit)에서는 `cdecl`, x86-64 SysV / Win64,
  AArch64 AAPCS64에서는 각 플랫폼의 표준 C 규약. 모든 export는 `extern "C"` 링키지.
- 정수는 `int32_t`, 실수는 IEEE-754 `double`(64-bit).
- 문자열은 **NUL 종단 UTF-8**. 고정 크기 버퍼에 복사되며 소유권 이전이 없다.
- 모든 함수는 `int32_t` 상태 코드를 반환한다(예외: `sil_free`는 `void`, `sil_abi_version`은 버전 정수).

## 3. Constants

```c
#define SIL_ABI_VERSION 1

/* status codes */
#define SIL_OK              0
#define SIL_ERR_ALLOC       1   /* 인스턴스 할당 실패 */
#define SIL_ERR_INVALID_ARG 2   /* NULL 포인터 등 */
#define SIL_ERR_RANGE       3   /* 포트 인덱스 범위 밖 */
#define SIL_ERR_STATE       4   /* 호출 순서 위반 */

/* port directions */
#define SIL_PORT_INPUT  0
#define SIL_PORT_OUTPUT 1

/* fixed string buffer sizes, including the NUL terminator */
#define SIL_NAME_MAX 64
#define SIL_UNIT_MAX 32
```

## 4. Port info struct

```c
typedef struct sil_port_info {
    int32_t index;                 /* 0-based, 배열 위치와 일치해야 한다 */
    int32_t direction;             /* SIL_PORT_INPUT | SIL_PORT_OUTPUT */
    char    name[SIL_NAME_MAX];    /* NUL-terminated UTF-8, 비어 있을 수 없다 */
    char    unit[SIL_UNIT_MAX];    /* NUL-terminated UTF-8, 무차원이면 빈 문자열 */
} sil_port_info_t;
```

레이아웃은 표준 C 구조체 정렬을 따른다(패킹 지시자 없음). 전체 크기 = 4 + 4 + 64 + 32 = 104 바이트,
정렬 4바이트. 로더는 이 크기를 검증하지 않고 필드 오프셋으로만 접근한다.

## 5. Entry points

```c
int32_t sil_abi_version(void);
int32_t sil_init(void** instance);
int32_t sil_step(void* instance, double dt);
int32_t sil_port_count(void* instance, int32_t* count);
int32_t sil_port_info(void* instance, int32_t index, sil_port_info_t* info);
int32_t sil_get(void* instance, int32_t index, double* value);
int32_t sil_set(void* instance, int32_t index, double value);
void    sil_free(void* instance);
```

### 계약

| 함수 | 계약 |
|---|---|
| `sil_abi_version` | `SIL_ABI_VERSION`을 반환. 인스턴스 없이 호출 가능. 로더가 가장 먼저 호출한다. |
| `sil_init` | 인스턴스를 할당하고 **t=0 상태**로 두며 출력 포트를 갱신한 뒤 `*instance`에 핸들을 쓴다. 같은 라이브러리에서 여러 번 호출해 여러 인스턴스를 만들 수 있어야 한다(전역 상태 금지). |
| `sil_step` | `dt`초만큼 한 스텝 전진하고 출력 포트를 갱신한다. `dt`는 유한한 양수. 런타임은 고정 주기이므로 한 실행 안에서 `dt`는 변하지 않는다. |
| `sil_port_count` | 포트 개수를 쓴다. `sil_init` 이후 값이 변하면 안 된다. |
| `sil_port_info` | `index`의 포트 선언을 채운다. 범위 밖이면 `SIL_ERR_RANGE`. |
| `sil_get` | 포트 현재값을 읽는다. 입력·출력 모두 읽을 수 있다. |
| `sil_set` | 포트 값을 쓴다. 출력 포트에 쓰는 것은 허용되지만 다음 `sil_step`이 덮어쓴다. |
| `sil_free` | 인스턴스를 해제한다. `NULL`은 무시(no-op). 해제 후 핸들 사용 금지. |

### 결정성 요구사항

같은 초기 상태와 같은 입력 쓰기 순서에 대해 출력 시퀀스가 **매 실행 동일**해야 한다.
난수, 벽시계 시각, 스레드, 파일·네트워크 I/O 사용 금지.

### 오류 처리

`sil_init` 이외의 함수가 `NULL` 인스턴스나 `NULL` 출력 포인터를 받으면 `SIL_ERR_INVALID_ARG`.
로더는 `SIL_OK` 이외의 반환값을 `SilNativeException`으로 올린다.

## 6. Loader lifecycle

1. `NativeLibrary.Load(path)`
2. `sil_abi_version()` — `SIL_ABI_VERSION`과 다르면 로드 거부
3. 필수 export 8개 해석 — 하나라도 없으면 로드 거부
4. `sil_init(&h)`
5. `sil_port_count` → `sil_port_info` 반복으로 포트 테이블 구성 (이름 중복·빈 이름·인덱스 불일치는 거부)
6. 주기마다 `sil_set`(입력) → `sil_step(dt)` → `sil_get`(출력)
7. `sil_free(h)` → 라이브러리 해제

## 7. Minimal example

```c
#include "sil_model.h"

typedef struct { double u, x; } inst_t;

static const sil_port_info_t PORTS[2] = {
    { 0, SIL_PORT_INPUT,  "u", "" },
    { 1, SIL_PORT_OUTPUT, "x", "" },
};

int32_t sil_abi_version(void) { return SIL_ABI_VERSION; }

int32_t sil_init(void** instance) {
    if (!instance) return SIL_ERR_INVALID_ARG;
    inst_t* s = (inst_t*)calloc(1, sizeof(inst_t));
    if (!s) return SIL_ERR_ALLOC;
    s->x = 1.0;              /* t=0 state */
    *instance = s;
    return SIL_OK;
}
/* ... sil_step / sil_port_* / sil_get / sil_set / sil_free ... */
```

전체 구현은 `src/Sil.NativeSpec/src/sil_first_order.c`를 볼 것.

## 8. Building the reference models

```sh
cc -O2 -std=c11 -shared -fPIC -Iinclude src/sil_first_order.c -o libsil_first_order.dylib   # macOS
cc -O2 -std=c11 -shared -fPIC -Iinclude src/sil_first_order.c -o libsil_first_order.so      # Linux
cl /O2 /LD /Iinclude src\sil_first_order.c /Fe:sil_first_order.dll                          # Windows (MSVC)
```

테스트는 실행 시점에 이용 가능한 C 컴파일러(`cc`/`gcc`/`clang`)로 이 소스를 빌드해 로더를 검증한다.
컴파일러가 없으면 테스트는 **건너뛰지 않고 실패**한다 — 규약 검증이 조용히 사라지면 안 된다.

## 9. 미구현 (v1 범위 밖)

- 파라미터 설정 진입점 없음. 모델 파라미터는 C 소스에 고정하거나 입력 포트로 받는다.
- 모델 이름 조회 없음. 인스턴스 이름은 런타임(C# 측)이 부여한다.
- `double` 이외의 포트 자료형 없음.
- FMI 2.0 FMU 임포트는 별개 게이트(PRD P1) — FMI 표준 문서와 공식 레퍼런스 FMU 확보 전까지 구현 금지.
