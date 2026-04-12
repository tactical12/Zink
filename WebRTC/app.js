let zinkConfig = null;
let pc = null;
let localStream = null;
let pendingIncoming = null;
let hasStartedMedia = false;

const statusEl = document.getElementById("status");
const localVideo = document.getElementById("localVideo");
const remoteVideo = document.getElementById("remoteVideo");

function setStatus(message) {
    statusEl.textContent = message;
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(JSON.stringify({
            type: "status",
            message: message
        }));
    }
}

window.initializeZinkCall = async function (config) {
    zinkConfig = config;
    setStatus("Media layer ready.");
};

window.zinkIncomingCall = function (payloadJson) {
    pendingIncoming = JSON.parse(payloadJson);
    setStatus("Incoming call ready to accept.");
};

window.zinkAcceptCall = async function () {
    await ensureMediaAndPeer();
    setStatus("Accepted call. Waiting for offer...");
};

window.zinkStartMediaForAcceptedCall = async function () {
    await ensureMediaAndPeer();
    await createAndSendOffer();
};

window.zinkReceiveOffer = async function (payloadJson) {
    const offerMsg = JSON.parse(payloadJson);
    await ensureMediaAndPeer();

    await pc.setRemoteDescription({
        type: offerMsg.sdpType || "offer",
        sdp: offerMsg.sdp
    });

    const answer = await pc.createAnswer();
    await pc.setLocalDescription(answer);

    postToHost({
        type: "answer",
        sdp: answer.sdp,
        sdpType: answer.type
    });

    setStatus("Offer received. Answer sent.");
};

window.zinkReceiveAnswer = async function (payloadJson) {
    const answerMsg = JSON.parse(payloadJson);

    if (!pc) return;

    await pc.setRemoteDescription({
        type: answerMsg.sdpType || "answer",
        sdp: answerMsg.sdp
    });

    setStatus("Answer received.");
};

window.zinkReceiveIce = async function (payloadJson) {
    const iceMsg = JSON.parse(payloadJson);

    if (!pc || !iceMsg.candidate) return;

    try {
        await pc.addIceCandidate({
            candidate: iceMsg.candidate,
            sdpMid: iceMsg.mid,
            sdpMLineIndex: iceMsg.mLineIndex
        });
    } catch {
    }
};

window.zinkEndCall = function () {
    try {
        if (pc) {
            pc.onicecandidate = null;
            pc.ontrack = null;
            pc.close();
            pc = null;
        }

        if (localStream) {
            localStream.getTracks().forEach(t => t.stop());
            localStream = null;
        }

        localVideo.srcObject = null;
        remoteVideo.srcObject = null;
        hasStartedMedia = false;
        pendingIncoming = null;

        setStatus("Media ended.");
    } catch {
    }
};

async function ensureMediaAndPeer() {
    if (hasStartedMedia && pc && localStream) {
        return;
    }

    if (!zinkConfig) {
        throw new Error("Call config not initialized.");
    }

    if (zinkConfig.isScreenShare) {
        localStream = await navigator.mediaDevices.getDisplayMedia({
            video: true,
            audio: true
        });
    } else {
        localStream = await navigator.mediaDevices.getUserMedia({
            audio: true,
            video: false
        });
    }

    localVideo.srcObject = localStream;

    pc = new RTCPeerConnection({
        iceServers: zinkConfig.iceServers || []
    });

    pc.onicecandidate = (event) => {
        if (!event.candidate) return;

        postToHost({
            type: "ice",
            candidate: event.candidate.candidate,
            mid: event.candidate.sdpMid,
            mLineIndex: event.candidate.sdpMLineIndex
        });
    };

    pc.ontrack = (event) => {
        if (event.streams && event.streams.length > 0) {
            remoteVideo.srcObject = event.streams[0];
        }
    };

    localStream.getTracks().forEach(track => {
        pc.addTrack(track, localStream);
    });

    hasStartedMedia = true;
    setStatus("Local media ready.");
}

async function createAndSendOffer() {
    if (!pc) {
        throw new Error("Peer connection not ready.");
    }

    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);

    postToHost({
        type: "offer",
        sdp: offer.sdp,
        sdpType: offer.type
    });

    setStatus("Offer created and sent.");
}

function postToHost(obj) {
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(JSON.stringify(obj));
    }
}