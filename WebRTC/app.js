let zinkConfig = null;
let pc = null;
let localStream = null;
let screenStream = null;
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
    ensurePeerConnection();
    setStatus("Accepted call. Waiting for offer...");
};

window.zinkStartMediaForAcceptedCall = async function () {
    await ensureMediaAndPeer();
    await createAndSendOffer();
};

window.zinkReceiveOffer = async function (payloadJson) {
    const offerMsg = JSON.parse(payloadJson);
    ensurePeerConnection();

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

        if (screenStream) {
            screenStream.getTracks().forEach(t => t.stop());
            screenStream = null;
        }

        localVideo.srcObject = null;
        remoteVideo.srcObject = null;
        hasStartedMedia = false;
        pendingIncoming = null;

        setStatus("Media ended.");
    } catch {
    }
};

window.zinkSetScreenShareEnabled = async function (enabled) {
    if (!zinkConfig) {
        throw new Error("Call config not initialized.");
    }

    zinkConfig.isScreenShare = enabled;
    await ensureMediaAndPeer();

    if (enabled) {
        await startScreenShareTrack();
    } else {
        await stopScreenShareTrack();
    }

    await createAndSendOffer();
};

async function ensureMediaAndPeer() {
    if (hasStartedMedia && pc && localStream) {
        return;
    }

    if (!zinkConfig) {
        throw new Error("Call config not initialized.");
    }

    if (zinkConfig.isScreenShare) {
        screenStream = await navigator.mediaDevices.getDisplayMedia({
            video: true,
            audio: false
        });
        localStream = new MediaStream([
            ...screenStream.getVideoTracks()
        ]);
        wireScreenTrackEnded();
    } else {
        localStream = await navigator.mediaDevices.getUserMedia({
            audio: true,
            video: false
        });
    }

    localVideo.srcObject = localStream;
    ensurePeerConnection();

    localStream.getTracks().forEach(track => {
        pc.addTrack(track, localStream);
    });

    hasStartedMedia = true;
    setStatus("Local media ready.");
}

function ensurePeerConnection() {
    if (pc) {
        return;
    }

    pc = new RTCPeerConnection({
        iceServers: (zinkConfig && zinkConfig.iceServers) || []
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

        if (event.track && event.track.kind === "video") {
            postToHost({
                type: "remote-video-ready"
            });
        }
    };
}

async function startScreenShareTrack() {
    if (screenStream && screenStream.getVideoTracks().some(track => track.readyState === "live")) {
        return;
    }

    screenStream = await navigator.mediaDevices.getDisplayMedia({
        video: true,
        audio: false
    });

    wireScreenTrackEnded();

    const screenTrack = screenStream.getVideoTracks()[0];
    if (!screenTrack) {
        throw new Error("No screen video track was selected.");
    }

    const sender = getVideoSender();
    if (sender) {
        await sender.replaceTrack(screenTrack);
    } else if (pc && localStream) {
        pc.addTrack(screenTrack, localStream);
    }

    if (localStream) {
        localStream.getVideoTracks().forEach(track => {
            if (track !== screenTrack) {
                track.stop();
                localStream.removeTrack(track);
            }
        });
        localStream.addTrack(screenTrack);
        localVideo.srcObject = localStream;
    }

    setStatus("Screen share started.");
}

async function stopScreenShareTrack() {
    const sender = getVideoSender();
    if (sender) {
        await sender.replaceTrack(null);
    }

    if (localStream) {
        localStream.getVideoTracks().forEach(track => {
            track.stop();
            localStream.removeTrack(track);
        });
        localVideo.srcObject = localStream;
    }

    if (screenStream) {
        screenStream.getTracks().forEach(track => track.stop());
        screenStream = null;
    }

    setStatus("Screen share stopped.");
}

function getVideoSender() {
    if (!pc) return null;
    return pc.getSenders().find(sender => sender.track && sender.track.kind === "video") || null;
}

function wireScreenTrackEnded() {
    if (!screenStream) return;

    screenStream.getVideoTracks().forEach(track => {
        track.onended = () => {
            postToHost({
                type: "screen-share-ended"
            });
        };
    });
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
