namespace TransactionalWindows.Core.Domain;

public enum TransactionState { Created, Starting, Running, Quiescing, DiffReady, Committing, Discarding, Completed, Failed, Aborted }
public enum FileChangeOperation { Create, Modify, Delete, Rename }
public enum RegistryChangeOperation { Create, Set, Delete, Rename }
public enum FileObjectType { File, Directory }
public enum RegistryHive { CurrentUser, LocalMachine }
public enum RegistryView { Default, View32, View64 }
public enum ProcessNodeStatus { Starting, Running, Exited, EscapeDetected, Terminated, Unknown }
public enum DiffItemType { File, Directory, RegistryValue, RegistrySubtree }
public enum DiffItemStatus { Applicable, Selected, Conflicted, Unsupported, Applied, Skipped, Failed }
public enum SelectionState { NotSelected, Selected, Required }
public enum ApplyState { NotApplied, Applied, Skipped, Failed }
public enum OperationDisposition { Supported, Unsupported, Blocked, Passthrough }
public enum ConflictPolicy { FailClosed }
public enum FilesystemScope { OneLocalNtfsVolume }
public enum RegistryScope { HkcuAndLimitedHklmSoftware }

