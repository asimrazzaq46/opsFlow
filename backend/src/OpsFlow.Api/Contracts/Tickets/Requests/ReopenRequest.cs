using System.ComponentModel.DataAnnotations;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Api.Contracts;

public sealed record ReopenRequest(int? Version);
