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

    [HttpPost("{id:guid}/chunks/{chunkNumber:int}")]
    public async Task<IActionResult> UploadChunk(Guid id, int chunkNumber, [FromHeader(Name = "X-FastDrop-Token")] string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token))
            return Unauthorized("Missing X-FastDrop-Token header.");

        // Notice we pass `Request.Body` directly to the service. 
        // We NEVER load the file into memory using IFormFile. We stream it dynamically!
        try
        {
            var response = await _transferService.UploadChunkAsync(id, token, chunkNumber, Request.Body, cancellationToken);
            if (response == null) return NotFound();

            return Ok(response);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> DownloadFile(
        Guid id,
        [FromHeader(Name = "X-FastDrop-Token")] string? headerToken,
        [FromQuery(Name = "access_token")] string? queryToken,
        CancellationToken cancellationToken)
    {
        // A header is preferred for API clients. A query token lets the web app
        // hand the request to the native browser download manager, which cannot
        // send custom headers and does not buffer the entire file in memory.
        var token = !string.IsNullOrWhiteSpace(headerToken) ? headerToken : queryToken;
        if (string.IsNullOrEmpty(token))
            return Unauthorized("Missing transfer token.");

        DownloadTransferResponse response;
        try
        {
            var result = await _transferService.InitiateDownloadAsync(id, token, cancellationToken);
            if (result == null) return NotFound();
            response = result;
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        // The stream is seekable, so ASP.NET Core can honor HTTP Range requests
        // and browsers can resume a large download after a dropped connection.
        var streamResult = new FileStreamResult(response.FileStream, response.ContentType)
        {
            FileDownloadName = response.FileName,
            EnableRangeProcessing = true
        };

        return streamResult;
    }
}
