using System.ComponentModel.DataAnnotations;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Api.Contracts;

public sealed record CategoryRequest([Required, StringLength(100, MinimumLength = 2)] string Name);
