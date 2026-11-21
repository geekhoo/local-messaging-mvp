export function publicCapabilities(status) {
  return {
    profileAcknowledgements: status?.capabilities?.profileAcknowledgements === true,
    participantRosters: status?.capabilities?.participantRosters === true,
    liveAcknowledgementSummaries: status?.capabilities?.liveAcknowledgementSummaries === true
  };
}

export const publicScopes = {
  message: { capability: "profileAcknowledgements", collection: "messages", resource: "acknowledgements", type: "acknowledgement", key: "messageId" },
  channel: { capability: "participantRosters", collection: "channels", resource: "participants", type: "participation", key: "channelId" },
  session: { capability: "participantRosters", collection: "sessions", resource: "participants", type: "participation", key: "sessionId" }
};

export function publicAction(kind, id, joined) {
  const { type, key } = publicScopes[kind];
  return kind === "message" ? { type, [key]: id } : { type, [key]: id, joined };
}

export function readPublicPage(api, { kind, id }, previous, signal) {
  const { collection, resource } = publicScopes[kind];
  return api.get(`${collection}/${encodeURIComponent(id)}/${resource}`, {
    limit: 25, asOf: previous?.snapshotCursor, pageToken: previous?.nextPageToken
  }, signal);
}
