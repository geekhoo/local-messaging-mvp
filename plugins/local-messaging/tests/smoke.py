"""Exercise MCP against a disposable production host. Run from the workspace root."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / 'server' / 'bin' / 'Release' / 'net10.0' / 'LocalMessaging.Plugin.dll'


def smoke(proc, *, legacy=False):
    call_id = 0

    def rpc(method, params=None, notify=False):
        nonlocal call_id
        call_id += 1
        frame = dict(jsonrpc='2.0', method=method)
        if not notify:
            frame['id'] = call_id
        if params is not None:
            frame['params'] = params
        proc.stdin.write(json.dumps(frame) + '\n')
        proc.stdin.flush()
        if not notify:
            response = json.loads(proc.stdout.readline())
            assert response.get('id') == call_id, response
            assert 'error' not in response, response
            return response['result']

    def tool(tool_name, *, expect_error=False, **args):
        result = rpc('tools/call', dict(name=tool_name, arguments=args))
        assert bool(result.get('isError')) == expect_error, result
        return result.get('structuredContent') or json.loads(result['content'][0]['text'])

    def created(receipt, kind):
        return next(row['id'] for row in receipt['created'] if row['type'] == kind)

    rpc('initialize', dict(protocolVersion='2025-06-18', capabilities={}, clientInfo=dict(name='smoke', version='1')))
    rpc('notifications/initialized', notify=True)
    tools = {entry['name']: entry for entry in rpc('tools/list')['tools']}
    assert {'subscribe', 'unsubscribe', 'join', 'leave', 'read_events', 'acknowledge_message',
            'list_acknowledgements', 'set_participation', 'list_participants'} <= tools.keys()
    for name in ('subscribe', 'unsubscribe', 'join', 'leave', 'acknowledge_message', 'set_participation'):
        assert tools[name]['annotations']['readOnlyHint'] is False
    assert tools['set_participation']['inputSchema']['properties']['joined']['type'] == 'boolean'
    capabilities = tool('status').get('capabilities', {})
    for flag in ('profileAcknowledgements', 'participantRosters'):
        assert (capabilities.get(flag) is True) == (not legacy), (
            f'Expected {flag} to be {"unavailable" if legacy else "enabled"}; host reports {capabilities}'
        )
    session_key = str(uuid.uuid4())
    profile = tool('connect_profile', claudeSessionId=session_key)
    user = profile.get('userId') or profile['id']
    assert tool('connect_profile', claudeSessionId=session_key)['userId'] == user
    portable = tool('connect_profile', profileId=session_key)
    assert portable['userId'] != user and portable['profile']['displayName'] == f'Agent {session_key}'
    assert tool('connect_profile', profileId=session_key)['userId'] == portable['userId']
    assert tool('connect_profile', expect_error=True)['code'] == 'invalid_arguments'
    assert tool('connect_profile', profileId=session_key, claudeSessionId=session_key,
                expect_error=True)['code'] == 'invalid_arguments'
    channel = created(tool('create_channel', userId=user, name='Smoke Test'), 'channel')
    session = created(tool('create_session', userId=user, channelId=channel, title='Review'), 'session')
    subscription = tool('subscribe', userId=user, sessionId=session)['subscription']['id']
    overlap = tool('join', userId=user, channelId=channel)['subscription']['id']
    text_args = dict(userId=user, channelId=channel, sessionId=session)
    request_id = str(uuid.uuid4())
    message = created(tool('send_message', **text_args, text='Hello', requestId=request_id), 'message')
    assert created(tool('send_message', **text_args, text='Hello', requestId=request_id), 'message') == message
    reply = created(tool('send_message', **text_args, text='Reply', parentMessageId=message), 'message')
    assert reply != message
    assert tool('get_thread', messageId=message)
    page = tool('list_subscriptions', userId=user, limit=1)
    second = tool('list_subscriptions', userId=user, limit=1, pageToken=page['nextPageToken'])
    assert {x['id'] for x in page['items'] + second['items'] if x['state'] == 'active'} == {subscription, overlap}
    for subscription_id in (subscription, overlap):
        read_args = dict(userId=user, subscriptionId=subscription_id)
        page = tool('read_events', **read_args, limit=1)
        assert [(x['eventType'], x['dataObject']['id']) for x in page['items']] == [('message.created', message)], page
        assert page['hasMore'] and page['nextCursor'], page
        next_page = tool('read_events', **read_args, after=page['nextCursor'])
        assert [(x['eventType'], x['dataObject']['id']) for x in next_page['items']] == [('message.created', reply)], next_page
        assert not next_page['hasMore'] and next_page['nextCursor'] != page['nextCursor'], next_page
        # Supplying after advances only this read, not the saved subscription.
        assert tool('read_events', **read_args, limit=1) == page
    for name, args in (
        ('list_participants', {}),
        ('list_participants', dict(channelId=channel, sessionId=session)),
        ('set_participation', dict(userId=user, sessionId=session)),
        ('set_participation', dict(userId=user, sessionId=session, joined='true')),
        ('set_participation', dict(userId=user, channelId=channel, sessionId=session, joined=True)),
    ):
        assert tool(name, expect_error=True, **args)['code'] == 'invalid_arguments'
    if legacy:
        for name, args in (
            ('list_acknowledgements', dict(messageId=message)),
            ('acknowledge_message', dict(userId=user, messageId=message)),
            ('list_participants', dict(channelId=channel)),
            ('set_participation', dict(userId=user, sessionId=session, joined=True)),
        ):
            error = tool(name, expect_error=True, **args)
            assert error['code'] == 'unsupported_capability' and 'requestId' not in error, error
        tool('unsubscribe', userId=user, subscriptionId=subscription)
    else:
        exercise_public_profiles(tool, user, channel, session, message, reply, subscription)
    error = tool('read_events', userId=user, subscriptionId=subscription, expect_error=True)
    assert error['code'] == 'subscription_closed' and error['status'] == 410, error
    assert tool('read_events', userId=user, subscriptionId=overlap)['items']
    assert tool('leave', userId=user, subscriptionId=overlap)['state'] == 'committed'
    error = tool('read_events', userId=user, subscriptionId=overlap, expect_error=True)
    assert error['code'] == 'subscription_closed', error
    print(f"MCP production HTTP/3 smoke test passed ({'legacy' if legacy else 'feature'} host).")


def exercise_public_profiles(tool, user, channel, session, message, reply, subscription):
    def committed(receipt, kind):
        assert receipt['state'] == 'committed' and receipt['operation'] == 'createObject', receipt
        return receipt['dataObjects'][kind]

    def profile_ids(items):
        for item in items:
            assert set(item['profile']) == {'id', 'handle', 'displayName'}, item
        return {item['profile']['id'] for item in items}

    def roster(**scope):
        return profile_ids(tool('list_participants', **scope)['items'])

    # History and replay above must not create public acknowledgements.
    for message_id in (message, reply):
        assert tool('list_acknowledgements', messageId=message_id)['items'] == []
    assert not roster(channelId=channel) and not roster(sessionId=session)
    other = tool('connect_profile', claudeSessionId=str(uuid.uuid4()))['userId']
    key = str(uuid.uuid4())
    command = dict(userId=user, messageId=message, requestId=key)
    ack = committed(tool('acknowledge_message', **command), 'acknowledgement')
    assert committed(tool('get_receipt', requestId=key), 'acknowledgement') == ack
    replay = tool('acknowledge_message', **command)
    assert replay['replayed'] is True and committed(replay, 'acknowledgement') == ack
    noop = tool('acknowledge_message', userId=user, messageId=message)
    assert not noop['created'] and committed(noop, 'acknowledgement') == ack
    tool('acknowledge_message', userId=user, messageId=str(uuid.uuid4()), expect_error=True)
    committed(tool('acknowledge_message', userId=other, messageId=message), 'acknowledgement')
    page = tool('list_acknowledgements', messageId=message, limit=1)
    next_page = tool('list_acknowledgements', messageId=message, limit=1,
                     pageToken=page['nextPageToken'], asOf=page['snapshotCursor'])
    items = page['items'] + next_page['items']
    assert len(items) == 2 and profile_ids(items) == {user, other}, items
    assert all(isinstance(x['acknowledgedUtcMs'], int) and x['acknowledgedUtcMs'] > 0 for x in items), items
    assert tool('list_acknowledgements', messageId=reply)['items'] == []

    def participate(joined, expected_channel_joined, **scope):
        receipt = tool('set_participation', userId=user, joined=joined, **scope)
        result = committed(receipt, 'participation')
        assert result['joined'] is joined and result['channelJoined'] is expected_channel_joined, result
        return receipt

    participate(True, True, sessionId=session)
    no_op = participate(False, True, channelId=channel)
    assert no_op['created'] == [] and no_op['eventRange'] is None, no_op
    assert roster(sessionId=session) == roster(channelId=channel) == {user}
    participate(True, True, channelId=channel)
    participate(False, True, sessionId=session)
    assert not roster(sessionId=session) and roster(channelId=channel) == {user}
    participate(True, True, sessionId=session)
    participate(False, True, channelId=channel)
    tool('unsubscribe', userId=user, subscriptionId=subscription)
    assert roster(sessionId=session) == roster(channelId=channel) == {user}
    committed(tool('set_participation', userId=other, sessionId=session, joined=True), 'participation')
    page = tool('list_participants', channelId=channel, limit=1)
    participate(False, False, sessionId=session)
    next_page = tool('list_participants', channelId=channel, limit=1,
                     pageToken=page['nextPageToken'], asOf=page['snapshotCursor'])
    assert profile_ids(page['items'] + next_page['items']) == {user, other}
    assert roster(channelId=channel) == roster(sessionId=session) == {other}


def main():
    if len(sys.argv) not in (4, 5) or (len(sys.argv) == 5 and sys.argv[4] != '--legacy'):
        raise SystemExit('usage: smoke.py HOST_EXE UNUSED_HTTP3_PORT UNUSED_FRONTEND_PORT [--legacy]')
    host_exe, m_port, f_port = sys.argv[1:4]
    legacy = len(sys.argv) == 5
    with tempfile.TemporaryDirectory(prefix='local-messaging-plugin-test-') as state:
        descriptor = Path(state) / 'endpoint.json'
        with subprocess.Popen([str(Path(host_exe).resolve()), '--state-dir', state,
                               '--messaging-port', m_port, '--frontend-port', f_port]) as host:
            try:
                for _ in range(150):
                    if descriptor.is_file():
                        break
                    if host.poll() is not None:
                        raise RuntimeError(f'Host exited during startup ({host.returncode})')
                    time.sleep(0.1)
                else:
                    raise RuntimeError('Timed out waiting for host descriptor')
                env = dict(os.environ, LOCAL_MESSAGING_ENDPOINT=str(descriptor))
                with subprocess.Popen(['dotnet', str(DLL)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                      text=True, encoding='utf-8', env=env) as proc:
                    try:
                        smoke(proc, legacy=legacy)
                    finally:
                        proc.kill()
            finally:
                host.terminate()
                host.wait(timeout=10)
                pointer = Path(state) / 'certificate.thumbprint'
                if pointer.is_file():
                    thumbprint = pointer.read_text().strip()
                    if len(thumbprint) == 40 and all(c in '0123456789abcdefABCDEF' for c in thumbprint):
                        subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command',
                                        f"Remove-Item -LiteralPath 'Cert:\\CurrentUser\\My\\{thumbprint}' "
                                        '-DeleteKey -ErrorAction Stop'], check=True, timeout=10)


if __name__ == '__main__':
    main()
