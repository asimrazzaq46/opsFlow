namespace OpsFlow.Application.Common;

/// <summary>Thrown when a write would cross the current tenant boundary. This is a bug, never a user error.</summary>
public sealed class TenantViolationException(string message) : InvalidOperationException(message);
