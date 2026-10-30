using System.Security.Claims;

namespace OpsFlow.Realtime.Chat;

public enum ChatFailure
{
    Invalid,
    Forbidden,
    Unauthenticated,
}
