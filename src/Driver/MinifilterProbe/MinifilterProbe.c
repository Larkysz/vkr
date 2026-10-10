#include <fltKernel.h>

static PFLT_FILTER gFilter;
static volatile LONG gOperationCounts[4];
static volatile LONG gTraceCount;

static ULONG
TwGetOperationIndex(_In_ UCHAR MajorFunction)
{
    switch (MajorFunction) {
    case IRP_MJ_CREATE:
        return 0;
    case IRP_MJ_WRITE:
        return 1;
    case IRP_MJ_SET_INFORMATION:
        return 2;
    case IRP_MJ_DIRECTORY_CONTROL:
        return 3;
    default:
        return MAXULONG;
    }
}

static FLT_PREOP_CALLBACK_STATUS
TwObserveOperation(
    _Inout_ PFLT_CALLBACK_DATA Data,
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _Outptr_result_maybenull_ PVOID* CompletionContext
)
{
    UNREFERENCED_PARAMETER(FltObjects);

    ULONG operation = TwGetOperationIndex(Data->Iopb->MajorFunction);
    if (operation < RTL_NUMBER_OF(gOperationCounts)) {
        LONG count = InterlockedIncrement(&gOperationCounts[operation]);
        LONG traceNumber = InterlockedIncrement(&gTraceCount);

        if (traceNumber <= 32) {
            DbgPrintEx(
                DPFLTR_IHVDRIVER_ID,
                DPFLTR_INFO_LEVEL,
                "[TWProbe] operation=%lu count=%ld pid=%p\n",
                operation,
                count,
                FltGetRequestorProcessId(Data)
            );
        }
    }

    *CompletionContext = NULL;
    return FLT_PREOP_SUCCESS_NO_CALLBACK;
}

static NTSTATUS
TwInstanceSetup(
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _In_ FLT_INSTANCE_SETUP_FLAGS Flags,
    _In_ DEVICE_TYPE VolumeDeviceType,
    _In_ FLT_FILESYSTEM_TYPE VolumeFilesystemType
)
{
    UNREFERENCED_PARAMETER(FltObjects);
    UNREFERENCED_PARAMETER(Flags);
    UNREFERENCED_PARAMETER(VolumeDeviceType);

    return VolumeFilesystemType == FLT_FSTYPE_NTFS
        ? STATUS_SUCCESS
        : STATUS_FLT_DO_NOT_ATTACH;
}

static NTSTATUS
TwUnload(_In_ FLT_FILTER_UNLOAD_FLAGS Flags)
{
    UNREFERENCED_PARAMETER(Flags);

    if (gFilter != NULL) {
        FltUnregisterFilter(gFilter);
        gFilter = NULL;
        DbgPrintEx(
            DPFLTR_IHVDRIVER_ID,
            DPFLTR_INFO_LEVEL,
            "[TWProbe] totals create=%ld write=%ld setinfo=%ld directory=%ld\n",
            gOperationCounts[0],
            gOperationCounts[1],
            gOperationCounts[2],
            gOperationCounts[3]
        );
    }

    return STATUS_SUCCESS;
}

static const FLT_OPERATION_REGISTRATION gOperations[] = {
    { IRP_MJ_CREATE, 0, TwObserveOperation, NULL },
    { IRP_MJ_WRITE, 0, TwObserveOperation, NULL },
    { IRP_MJ_SET_INFORMATION, 0, TwObserveOperation, NULL },
    { IRP_MJ_DIRECTORY_CONTROL, 0, TwObserveOperation, NULL },
    { IRP_MJ_OPERATION_END }
};

static const FLT_REGISTRATION gRegistration = {
    sizeof(FLT_REGISTRATION),
    FLT_REGISTRATION_VERSION,
    0,
    NULL,
    gOperations,
    TwUnload,
    TwInstanceSetup,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL
};

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
)
{
    NTSTATUS status;

    UNREFERENCED_PARAMETER(RegistryPath);

    status = FltRegisterFilter(DriverObject, &gRegistration, &gFilter);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    status = FltStartFiltering(gFilter);
    if (!NT_SUCCESS(status)) {
        FltUnregisterFilter(gFilter);
        gFilter = NULL;
    }

    return status;
}
