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
    public async Task<IActionResult> DownloadFile(Guid id, [FromHeader(Name = "X-FastDrop-Token")] string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token))
            return Unauthorized("Missing X-FastDrop-Token header.");

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

        // Ensure the file stream is disposed after the response is fully sent.
        Response.RegisterForDispose(response.FileStream);

        // Set Content-Length explicitly so the browser knows the total size for progress bars.
        // Without this, the browser shows "unknown size" and the download appears to hang.
        Response.ContentLength = response.Size;
        
        // Set Content-Disposition so the browser knows the real filename to save.
        // "attachment" tells the browser to download rather than display in-tab.
        Response.Headers.ContentDisposition = $"attachment; filename=\"{response.FileName}\"";

        // Returning a FileStreamResult streams the body directly without buffering in memory.
        var streamResult = new FileStreamResult(response.FileStream, response.ContentType)
        {
            FileDownloadName = response.FileName,
            EnableRangeProcessing = false // Must be false because CompositeStream is forward-only
        };

        // Fire-and-forget the completion after the response is fully written.
        Response.OnCompleted(async () =>
        {
            await _transferService.CompleteDownloadAsync(id, CancellationToken.None);
        });

        return streamResult;
    }
}
