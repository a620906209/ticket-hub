namespace ProjectC.Application.Common;

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Unauthorized,
    Forbidden,
    QueueAdmissionRequired,
    InvalidTicketSignature,
}
