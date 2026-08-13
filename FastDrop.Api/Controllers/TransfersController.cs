using FastDrop.Application.DTOs;
using FastDrop.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace FastDrop.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class TransfersController : ControllerBase
{
    private readonly ITransferService _transferService;

    // The Controller only knows about the ITransferService interface.
    // It has NO IDEA about EF Core or SQL Server.
    public TransfersController(ITransferService transferService)
    {
        _transferService = transferService;
    }

    [HttpPost]
    public async Task<IActionResult> CreateTransfer([FromBody] CreateTransferRequest request, CancellationToken cancellationToken)
    {
        var response = await _transferService.CreateTransferAsync(request, cancellationToken);
        
        // Return a 201 Created status, pointing to the GetTransfer endpoint
        return CreatedAtAction(nameof(GetTransfer), new { id = response.TransferId }, response);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTransfer(Guid id, CancellationToken cancellationToken)
    {
        var response = await _transferService.GetTransferAsync(id, cancellationToken);
        
        if (response == null)
            return NotFound();

        return Ok(response);
    }

    [HttpPost("{id:guid}/join")]
    public async Task<IActionResult> JoinTransfer(Guid id, [FromBody] JoinTransferRequest request, CancellationToken cancellationToken)
    {
        var success = await _transferService.JoinTransferAsync(id, request, cancellationToken);
        
        if (!success)
            return Forbid(); // 403 Forbidden (wrong token) or 404 Not Found (bad ID)

        return Ok(new { Message = "Successfully joined the transfer." });
    }
}
