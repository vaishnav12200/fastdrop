const CHUNK_SIZE = 10 * 1024 * 1024; // 10MB chunks
const API_BASE = '/api/v1/Transfers';

// UI Elements
const views = {
    upload: document.getElementById('view-upload'),
    waiting: document.getElementById('view-waiting'),
    progress: document.getElementById('view-progress'),
    success: document.getElementById('view-success')
};

const dropzone = document.getElementById('dropzone');
const fileInput = document.getElementById('file-input');
const shareLinkInput = document.getElementById('share-link');
const copyBtn = document.getElementById('copy-btn');
const progressBar = document.getElementById('progress-bar');
const progressPercent = document.getElementById('progress-percent');
const progressChunks = document.getElementById('progress-chunks');
const statusMessage = document.getElementById('status-message');
const fileNameDisplay = document.getElementById('file-name-display');
const progressTitle = document.getElementById('progress-title');

// State
let currentFile = null;
let currentTransfer = null;
let pollInterval = null;

// Routing based on URL (Receiver Flow)
window.addEventListener('DOMContentLoaded', () => {
    const urlParams = new URLSearchParams(window.location.search);
    const transferId = urlParams.get('id');
    const token = urlParams.get('token');

    if (transferId && token) {
        startReceiverFlow(transferId, token);
    }
});

function switchView(viewName) {
    Object.values(views).forEach(v => {
        v.classList.remove('active');
        v.classList.add('hidden');
    });
    views[viewName].classList.remove('hidden');
    views[viewName].classList.add('active');
}

// --- SENDER FLOW ---

dropzone.addEventListener('click', () => fileInput.click());
dropzone.addEventListener('dragover', (e) => {
    e.preventDefault();
    dropzone.classList.add('dragover');
});
dropzone.addEventListener('dragleave', () => dropzone.classList.remove('dragover'));
dropzone.addEventListener('drop', (e) => {
    e.preventDefault();
    dropzone.classList.remove('dragover');
    if (e.dataTransfer.files.length > 0) handleFileSelection(e.dataTransfer.files[0]);
});
fileInput.addEventListener('change', (e) => {
    if (e.target.files.length > 0) handleFileSelection(e.target.files[0]);
});

async function handleFileSelection(file) {
    currentFile = file;
    const totalChunks = Math.ceil(file.size / CHUNK_SIZE);

    try {
        const response = await fetch(API_BASE, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                fileName: file.name,
                size: file.size,
                contentType: file.type || 'application/octet-stream',
                totalChunks: totalChunks,
                chunkSize: CHUNK_SIZE
            })
        });

        if (!response.ok) throw new Error(`Server error: ${response.status}`);
        currentTransfer = await response.json();

        console.log('Transfer created:', currentTransfer);

        // API returns TransferId (PascalCase), ASP.NET serializes as camelCase -> transferId
        const shareUrl = `${window.location.origin}/?id=${currentTransfer.transferId}&token=${encodeURIComponent(currentTransfer.receiverToken)}`;
        shareLinkInput.value = shareUrl;

        switchView('waiting');
        startPollingForReceiver();
    } catch (err) {
        alert('Error creating transfer: ' + err.message);
    }
}

copyBtn.addEventListener('click', () => {
    navigator.clipboard.writeText(shareLinkInput.value).catch(() => {
        shareLinkInput.select();
        document.execCommand('copy');
    });
    copyBtn.textContent = 'Copied!';
    setTimeout(() => copyBtn.textContent = 'Copy', 2000);
});

function startPollingForReceiver() {
    pollInterval = setInterval(async () => {
        try {
            const res = await fetch(`${API_BASE}/${currentTransfer.transferId}`);
            if (!res.ok) return;
            const data = await res.json();

            if (data.status === 'ReceiverConnected' || data.status === 'Uploading') {
                clearInterval(pollInterval);
                startUploadProcess();
            }
        } catch (e) { console.error('Polling error:', e); }
    }, 2000);
}

async function startUploadProcess() {
    switchView('progress');
    fileNameDisplay.textContent = currentFile.name;
    progressTitle.textContent = 'Uploading...';
    statusMessage.textContent = 'Sending chunks to server...';

    const totalChunks = Math.ceil(currentFile.size / CHUNK_SIZE);
    let uploadedChunks = 0;
    let currentChunk = 0;
    let hasFailed = false;

    const uploadNextChunk = async () => {
        if (currentChunk >= totalChunks || hasFailed) return;

        const chunkIndex = currentChunk++;
        const start = chunkIndex * CHUNK_SIZE;
        const end = Math.min(start + CHUNK_SIZE, currentFile.size);
        const chunkBlob = currentFile.slice(start, end);

        try {
            const res = await fetch(`${API_BASE}/${currentTransfer.transferId}/chunks/${chunkIndex}`, {
                method: 'POST',
                headers: {
                    // The controller reads from header: [FromHeader(Name = "X-FastDrop-Token")]
                    'X-FastDrop-Token': currentTransfer.senderToken,
                    'Content-Type': 'application/octet-stream'
                },
                body: chunkBlob
            });

            if (!res.ok) {
                const errText = await res.text();
                throw new Error(`Chunk ${chunkIndex} failed: ${res.status} ${errText}`);
            }

            uploadedChunks++;
            updateProgress(uploadedChunks, totalChunks);
            statusMessage.textContent = `Uploaded ${uploadedChunks} of ${totalChunks} chunks...`;

            // Chain next chunk recursively
            await uploadNextChunk();
        } catch (e) {
            hasFailed = true;
            console.error(e);
            alert('Upload failed: ' + e.message);
        }
    };

    // Start pool of concurrent workers (4 at a time)
    const workers = [];
    for (let i = 0; i < Math.min(4, totalChunks); i++) {
        workers.push(uploadNextChunk());
    }
    await Promise.all(workers);

    if (!hasFailed) {
        statusMessage.textContent = 'All chunks uploaded!';
        switchView('success');
    }
}

// --- RECEIVER FLOW ---

async function startReceiverFlow(transferId, token) {
    // Decode token in case it was URL-encoded
    const decodedToken = decodeURIComponent(token);

    switchView('progress');
    progressTitle.textContent = 'Connecting...';
    statusMessage.textContent = 'Joining transfer session...';

    try {
        // Join: send receiverToken in body (ASP.NET Core JSON is case-insensitive)
        const joinRes = await fetch(`${API_BASE}/${transferId}/join`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ receiverToken: decodedToken })
        });

        if (!joinRes.ok) {
            const statusCode = joinRes.status;
            console.error(`Join failed with status ${statusCode}`);
            alert(`Invalid or expired transfer link. (${statusCode})`);
            window.location.href = '/';
            return;
        }

        statusMessage.textContent = 'Waiting for sender to start uploading...';

        // Poll until transfer is Ready
        pollInterval = setInterval(async () => {
            try {
                const res = await fetch(`${API_BASE}/${transferId}`);
                if (!res.ok) return;
                const data = await res.json();

                if (data.fileName) fileNameDisplay.textContent = data.fileName;

                if (data.status === 'Ready' || data.status === 'Downloading') {
                    clearInterval(pollInterval);
                    initiateDownload(transferId, decodedToken, data.fileName);
                } else if (data.status === 'Uploading') {
                    progressTitle.textContent = 'Receiving...';
                    statusMessage.textContent = 'Sender is uploading. Please wait...';
                    progressBar.style.width = '80%';
                    progressPercent.textContent = 'Uploading';
                    progressChunks.textContent = '';
                }
            } catch (e) { console.error('Polling error:', e); }
        }, 2000);

    } catch (err) {
        alert('Failed to connect: ' + err.message);
        window.location.href = '/';
    }
}

async function initiateDownload(transferId, token, fileName) {
    progressTitle.textContent = 'Downloading!';
    statusMessage.textContent = 'Your download is starting...';
    progressBar.style.width = '0%';
    progressPercent.textContent = '0%';

    try {
        const res = await fetch(`${API_BASE}/${transferId}/download`, {
            method: 'GET',
            headers: { 'X-FastDrop-Token': token }
        });

        if (!res.ok) {
            throw new Error(`Download failed: ${res.status} ${await res.text()}`);
        }

        // Get filename from Content-Disposition header if available
        const disposition = res.headers.get('Content-Disposition');
        let downloadName = fileName || 'download';
        if (disposition) {
            const match = disposition.match(/filename="?([^"]+)"?/);
            if (match) downloadName = match[1];
        }

        const contentLength = res.headers.get('Content-Length');
        const totalBytes = contentLength ? parseInt(contentLength, 10) : 0;
        let receivedBytes = 0;

        // Stream directly to disk via a Service Worker / native stream approach.
        // We collect chunks into an array and track progress — much more memory efficient
        // than await res.blob() which loads the ENTIRE file into RAM before saving.
        const chunks = [];
        const reader = res.body.getReader();

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;

            chunks.push(value);
            receivedBytes += value.byteLength;

            if (totalBytes > 0) {
                const percent = Math.round((receivedBytes / totalBytes) * 100);
                progressBar.style.width = `${percent}%`;
                progressPercent.textContent = `${percent}%`;
                const mbReceived = (receivedBytes / 1024 / 1024).toFixed(1);
                const mbTotal = (totalBytes / 1024 / 1024).toFixed(1);
                statusMessage.textContent = `${mbReceived} MB / ${mbTotal} MB`;
            } else {
                // Unknown total size — just show received amount
                progressBar.style.width = '80%';
                const mb = (receivedBytes / 1024 / 1024).toFixed(1);
                statusMessage.textContent = `Received ${mb} MB...`;
            }
        }

        // All bytes received — now create blob and trigger save dialog
        const blob = new Blob(chunks);
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.style.display = 'none';
        a.href = url;
        a.download = downloadName;
        document.body.appendChild(a);
        a.click();
        // Delay revocation slightly to ensure download dialog has opened
        setTimeout(() => {
            window.URL.revokeObjectURL(url);
            document.body.removeChild(a);
        }, 1000);

        progressBar.style.width = '100%';
        progressPercent.textContent = '100%';
        statusMessage.textContent = 'Download complete!';

        setTimeout(() => switchView('success'), 800);
        document.querySelector('#view-success p').textContent = 'File successfully received!';

    } catch (e) {
        console.error(e);
        alert('Download failed: ' + e.message);
    }
}

function updateProgress(current, total) {
    const percent = Math.round((current / total) * 100);
    progressBar.style.width = `${percent}%`;
    progressPercent.textContent = `${percent}%`;
    progressChunks.textContent = `${current} / ${total} chunks`;
}
