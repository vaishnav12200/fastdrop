const CHUNK_SIZE = 16 * 1024 * 1024; // 16 MiB chunks reduce request overhead for large transfers
const MAX_UPLOAD_WORKERS = 6;
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

        // A receiver link is deliberately not available yet. Upload to the
        // server quarantine first; it is generated only after malware scanning.
        await startUploadProcess();
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

    // Six concurrent requests generally fill a broadband connection better than
    // one request, while keeping browser memory bounded to roughly 96 MiB.
    const workers = [];
    for (let i = 0; i < Math.min(MAX_UPLOAD_WORKERS, totalChunks); i++) {
        workers.push(uploadNextChunk());
    }
    await Promise.all(workers);

    if (!hasFailed) {
        progressTitle.textContent = 'Scanning file...';
        statusMessage.textContent = 'Checking the completed file for known threats before creating the share link.';
        progressChunks.textContent = 'Security scan in progress';
        await waitForScanAndPublish();
    }
}

async function waitForScanAndPublish() {
    while (true) {
        await new Promise(resolve => setTimeout(resolve, 2000));
        try {
            const statusResponse = await fetch(`${API_BASE}/${currentTransfer.transferId}`);
            if (!statusResponse.ok) throw new Error(`Could not check scan status: ${statusResponse.status}`);
            const transfer = await statusResponse.json();

            if (transfer.status === 'Blocked') {
                throw new Error('This file was blocked by the malware scanner and cannot be shared.');
            }
            if (transfer.status !== 'Clean') continue;

            const publishResponse = await fetch(`${API_BASE}/${currentTransfer.transferId}/publish`, {
                method: 'POST',
                headers: { 'X-FastDrop-Token': currentTransfer.senderToken }
            });
            if (!publishResponse.ok) throw new Error(`Could not create secure link: ${await publishResponse.text()}`);

            const share = await publishResponse.json();
            shareLinkInput.value = `${window.location.origin}/?id=${currentTransfer.transferId}&token=${encodeURIComponent(share.receiverToken)}`;
            switchView('waiting');
            return;
        } catch (error) {
            console.error(error);
            alert(error.message);
            switchView('upload');
            return;
        }
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

        // Links only exist after a clean scan and a complete upload. Joining
        // therefore authorizes an immediately available download.
        initiateDownload(transferId, decodedToken);

    } catch (err) {
        alert('Failed to connect: ' + err.message);
        window.location.href = '/';
    }
}

async function initiateDownload(transferId, token, fileName) {
    progressTitle.textContent = 'Downloading!';
    statusMessage.textContent = 'Starting download...';
    progressBar.style.width = '100%';
    progressPercent.textContent = '100%';

    try {
        // Fetching then creating a Blob holds every byte in the tab's RAM. The
        // native download manager streams to disk and can resume Range responses.
        const downloadUrl = `${API_BASE}/${transferId}/download?access_token=${encodeURIComponent(token)}`;
        const a = document.createElement('a');
        a.style.display = 'none';
        a.href = downloadUrl;
        document.body.appendChild(a);
        a.click();
        setTimeout(() => document.body.removeChild(a), 1000);

        progressBar.style.width = '100%';
        progressPercent.textContent = '100%';
        statusMessage.textContent = 'Download started in your browser.';

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
