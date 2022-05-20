﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SFTP.Enums;
using SFTP.Exceptions;
using SFTP.IO;
using SFTP.Models;

namespace SFTP;

public sealed class SFTPServer : ISFTPServer
{
    private readonly SFTPServerOptions _options;
    private readonly ILogger<SFTPServer> _logger;
    private readonly SshStreamReader _reader;
    private readonly SshStreamWriter _writer;
    private readonly ISFTPHandler _sftphandler;
    private uint _protocolversion = 0;

    private readonly Dictionary<RequestType, Func<uint, CancellationToken, Task>> _messagehandlers;

    public SFTPServer(IOptions<SFTPServerOptions> options, ILogger<SFTPServer> logger, Stream @in, Stream @out)
        : this(options, logger, @in, @out, new DefaultSFTPHandler()) { }

    public SFTPServer(IOptions<SFTPServerOptions> options, ILogger<SFTPServer> logger, Stream @in, Stream @out, ISFTPHandler sftpHandler)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _reader = new SshStreamReader(@in ?? throw new ArgumentNullException(nameof(@in)));
        _writer = new SshStreamWriter(@out ?? throw new ArgumentNullException(nameof(@out)), _options.MaxMessageSize);
        _sftphandler = sftpHandler ?? throw new ArgumentNullException(nameof(sftpHandler));

        _messagehandlers = new()
        {
            { RequestType.OPEN, OpenHandler },
            { RequestType.CLOSE, CloseHandler },
            { RequestType.READ, ReadHandler },
            { RequestType.WRITE, WriteHandler },
            { RequestType.LSTAT, LStatHandler },
            { RequestType.FSTAT, FStatHandler },
            { RequestType.SETSTAT, SetStatHandler },
            { RequestType.FSETSTAT, FSetStatHandler },
            { RequestType.OPENDIR, OpenDirHandler },
            { RequestType.READDIR, ReadDirHandler },
            { RequestType.REMOVE, RemoveHandler },
            { RequestType.MKDIR, MakeDirHandler },
            { RequestType.RMDIR, RemoveDirHandler },
            { RequestType.REALPATH, RealPathHandler },
            { RequestType.STAT, StatHandler },
            { RequestType.RENAME, RenameHandler },
#if NET6_0_OR_GREATER
            { RequestType.READLINK, ReadLinkHandler },
            { RequestType.SYMLINK, SymLinkHandler },
#endif
            { RequestType.EXTENDED, ExtendedHandler }
        };
    }

    public async Task Run(CancellationToken cancellationToken = default)
    {
        uint msglength;
        do
        {
            msglength = await _reader.ReadUInt32(cancellationToken).ConfigureAwait(false);
            if (msglength > 0)
            {
                // Determine message type
                var msgtype = (RequestType)await _reader.ReadByte(cancellationToken).ConfigureAwait(false);
                if (_protocolversion == 0 && msgtype is RequestType.INIT)
                {
                    // We subtract 5 bytes (1 for requesttype and 4 for protocolversion) from msglength and pass the
                    // remainder so the inithandler can parse extensions (if any)
                    await InitHandler(msglength - 5, cancellationToken).ConfigureAwait(false);
                }
                else if (_protocolversion > 0)
                {
                    // Get requestid
                    var requestid = await _reader.ReadUInt32(cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("{msgtype} [ID: {requestid} LEN: {msglength}]", msgtype, requestid, msglength);

                    // Get handler and handle the message when supported
                    if (_messagehandlers.TryGetValue(msgtype, out var handler))
                    {
                        try
                        {
                            await handler(requestid, cancellationToken).ConfigureAwait(false);
                        }
                        catch (HandlerException ex)
                        {
                            _logger.Log(ex.LogLevel, ex, "SFTPHandler exception");
                            await SendStatus(requestid, ex.Status, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "An error occured");
                            await SendStatus(requestid, Status.FAILURE, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await SendStatus(requestid, Status.OP_UNSUPPORTED, cancellationToken).ConfigureAwait(false);
                    }
                }

                // Write response
                await _writer.Flush(cancellationToken).ConfigureAwait(false);
            }
        } while (!cancellationToken.IsCancellationRequested && msglength > 0);
    }


    private async Task InitHandler(uint extensiondatalength, CancellationToken cancellationToken = default)
    {
        // Get client version
        var clientversion = await _reader.ReadUInt32(cancellationToken).ConfigureAwait(false);
        _protocolversion = Math.Min(clientversion, 3);

        // Get client extensions (if any)
        var clientextensions = new Dictionary<string, string>();
        _logger.LogInformation("LEN: {len}", extensiondatalength);
        while (extensiondatalength > 0)
        {
            var name = await _reader.ReadString(cancellationToken).ConfigureAwait(false);
            var data = await _reader.ReadString(cancellationToken).ConfigureAwait(false);
            extensiondatalength -= (uint)(name.Length + data.Length);
        }

        var serverextensions = await _sftphandler.Init(clientversion, Environment.UserName, new SFTPExtensions(clientextensions)).ConfigureAwait(false);

        // Send version response
        await _writer.Write(RequestType.VERSION, cancellationToken).ConfigureAwait(false);
        await _writer.Write(_protocolversion, cancellationToken).ConfigureAwait(false);
        foreach (var e in serverextensions)
        {
            await _writer.Write(e.Key).ConfigureAwait(false);
            await _writer.Write(e.Value).ConfigureAwait(false);
        }
    }

    private async Task OpenHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var path = await _reader.ReadString(cancellationToken).ConfigureAwait(false);
        var flags = await _reader.ReadAccessFlags(cancellationToken).ConfigureAwait(false);
        var attrs = await _reader.ReadAttributes(cancellationToken).ConfigureAwait(false);
        var result = await _sftphandler.Open(_sftphandler.GetPath(new SFTPPath(_options.Root), new SFTPPath(path)), flags.ToFileMode(), flags.ToFileAccess(), attrs, cancellationToken).ConfigureAwait(false);
        await SendHandle(requestId, result.Handle, cancellationToken).ConfigureAwait(false);
    }

    private async Task CloseHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var handle = new SFTPHandle(await _reader.ReadString(cancellationToken).ConfigureAwait(false));
        await _sftphandler.Close(handle, cancellationToken).ConfigureAwait(false);
        await SendStatus(requestId, Status.OK, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var handle = new SFTPHandle(await _reader.ReadString(cancellationToken).ConfigureAwait(false));
        var offset = await _reader.ReadUInt64(cancellationToken).ConfigureAwait(false);
        var len = await _reader.ReadUInt32(cancellationToken).ConfigureAwait(false);
        var result = await _sftphandler.Read(handle, offset, len, cancellationToken).ConfigureAwait(false);

        if (result.Status == Status.OK)
        {
            await _writer.Write(ResponseType.DATA, cancellationToken).ConfigureAwait(false);
            await _writer.Write(requestId, cancellationToken).ConfigureAwait(false);
            await _writer.Write(result.Data.Length, cancellationToken).ConfigureAwait(false);
            await _writer.Write(result.Data, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SendStatus(requestId, result.Status, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var handle = new SFTPHandle(await _reader.ReadString(cancellationToken).ConfigureAwait(false));
        var offset = await _reader.ReadUInt64(cancellationToken).ConfigureAwait(false);
        var data = await _reader.ReadBinary(cancellationToken).ConfigureAwait(false);
        await _sftphandler.Write(handle, offset, data, cancellationToken).ConfigureAwait(false);
        await SendStatus(requestId, Status.OK, cancellationToken).ConfigureAwait(false);
    }

    private async Task LStatHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var path = _sftphandler.GetPath(new SFTPPath(_options.Root), new SFTPPath(await _reader.ReadString(cancellationToken).ConfigureAwait(false)));
        var result = await _sftphandler.LStat(path, cancellationToken).ConfigureAwait(false);
        await SendStat(requestId, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task FStatHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var handle = new SFTPHandle(await _reader.ReadString(cancellationToken).ConfigureAwait(false));
        var result = await _sftphandler.FStat(handle, cancellationToken).ConfigureAwait(false);
        await SendStat(requestId, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetStatHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var path = _sftphandler.GetPath(new SFTPPath(_options.Root), new SFTPPath(await _reader.ReadString(cancellationToken).ConfigureAwait(false)));
        var attrs = await _reader.ReadAttributes(cancellationToken).ConfigureAwait(false);
        await _sftphandler.SetStat(path, attrs, cancellationToken).ConfigureAwait(false);
        await SendStatus(requestId, Status.OK, cancellationToken).ConfigureAwait(false);
    }

    private async Task FSetStatHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var handle = new SFTPHandle(await _reader.ReadString(cancellationToken).ConfigureAwait(false));
        var attrs = await _reader.ReadAttributes(cancellationToken).ConfigureAwait(false);
        await _sftphandler.FSetStat(handle, attrs, cancellationToken).ConfigureAwait(false);
        await SendStatus(requestId, Status.OK, cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenDirHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var path = _sftphandler.GetPath(new SFTPPath(_options.Root), new SFTPPath(await _reader.ReadString(cancellationToken).ConfigureAwait(false)));
        var result = await _sftphandler.OpenDir(path, cancellationToken).ConfigureAwait(false);
        await SendHandle(requestId, result.Handle, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadDirHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var handle = new SFTPHandle(await _reader.ReadString(cancellationToken).ConfigureAwait(false));
        var result = await _sftphandler.ReadDir(handle, cancellationToken).ConfigureAwait(false);

        if (result.Status == Status.OK)
        {
            await _writer.Write(ResponseType.NAME, cancellationToken).ConfigureAwait(false);
            await _writer.Write(requestId, cancellationToken).ConfigureAwait(false);
            await _writer.Write(result.Names.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SendStatus(requestId, result.Status, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RemoveHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var path = _sftphandler.GetPath(new SFTPPath(_options.Root), new SFTPPath(await _reader.ReadString(cancellationToken).ConfigureAwait(false)));
        await _sftphandler.Remove(path, cancellationToken).ConfigureAwait(false);
        await SendStatus(requestId, Status.OK, cancellationToken).ConfigureAwait(false);
    }

    private async Task MakeDirHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var path = _sftphandler.GetPath(new SFTPPath(_options.Root), new SFTPPath(await _reader.ReadString(cancellationToken).ConfigureAwait(false)));
        var attrs = await _reader.ReadAttributes(cancellationToken).ConfigureAwait(false);
        await _sftphandler.MakeDir(path, attrs, cancellationToken).ConfigureAwait(false);
        await SendStatus(requestId, Status.OK, cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveDirHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var path = _sftphandler.GetPath(new SFTPPath(_options.Root), new SFTPPath(await _reader.ReadString(cancellationToken).ConfigureAwait(false)));
        await _sftphandler.RemoveDir(path, cancellationToken).ConfigureAwait(false);
        await SendStatus(requestId, Status.OK, cancellationToken).ConfigureAwait(false);
    }

    private async Task RealPathHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var path = await _reader.ReadString(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(path) || path == ".")
        {
            path = "/";
        }
        var result = await _sftphandler.RealPath(new SFTPPath(path), cancellationToken).ConfigureAwait(false);

        await _writer.Write(ResponseType.NAME, cancellationToken).ConfigureAwait(false);
        await _writer.Write(requestId, cancellationToken).ConfigureAwait(false);
        await _writer.Write(new[] { SFTPName.FromString(result.Path) }, cancellationToken).ConfigureAwait(false);
    }

    private async Task StatHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var path = _sftphandler.GetPath(new SFTPPath(_options.Root), new SFTPPath(await _reader.ReadString(cancellationToken).ConfigureAwait(false)));
        var result = await _sftphandler.Stat(path, cancellationToken).ConfigureAwait(false);
        await SendStat(requestId, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task RenameHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var oldpath = _sftphandler.GetPath(new SFTPPath(_options.Root), new SFTPPath(await _reader.ReadString(cancellationToken).ConfigureAwait(false)));
        var newpath = _sftphandler.GetPath(new SFTPPath(_options.Root), new SFTPPath(await _reader.ReadString(cancellationToken).ConfigureAwait(false)));
        await _sftphandler.Rename(oldpath, newpath, cancellationToken).ConfigureAwait(false);
        await SendStatus(requestId, Status.OK, cancellationToken).ConfigureAwait(false);
    }

#if NET6_0_OR_GREATER
    private async Task ReadLinkHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var path = _sftphandler.GetPath(new SFTPPath(_options.Root), new SFTPPath(await _reader.ReadString(cancellationToken).ConfigureAwait(false)));
        var result = await _sftphandler.ReadLink(path, cancellationToken).ConfigureAwait(false);

        await _writer.Write(ResponseType.NAME, cancellationToken).ConfigureAwait(false);
        await _writer.Write(requestId, cancellationToken).ConfigureAwait(false);
        await _writer.Write(new[] { result }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SymLinkHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        //NOTE: target and link appear to be swapped from the RFC??
        //Tested with sftp (commandline tool), WinSCP and CyberDuck
        var targetpath = _sftphandler.GetPath(new SFTPPath(_options.Root), new SFTPPath(await _reader.ReadString(cancellationToken).ConfigureAwait(false)));
        var linkpath = _sftphandler.GetPath(new SFTPPath(_options.Root), new SFTPPath(await _reader.ReadString(cancellationToken).ConfigureAwait(false)));

        await _sftphandler.SymLink(linkpath, targetpath, cancellationToken).ConfigureAwait(false);
        await SendStatus(requestId, Status.OK, cancellationToken).ConfigureAwait(false);
    }
#endif

    private async Task ExtendedHandler(uint requestId, CancellationToken cancellationToken = default)
    {
        var name = await _reader.ReadString(cancellationToken).ConfigureAwait(false);

        // Make sure we already output the requestId, the handler will have access to the output stream to write
        // arbitrary data after this
        await _writer.Write(requestId, cancellationToken).ConfigureAwait(false);
        // Now the handler will have access to both our in- and out-streams
        await _sftphandler.Extended(name, _reader.Stream, _writer.Stream).ConfigureAwait(false);
    }

    private async Task SendHandle(uint requestId, string handle, CancellationToken cancellationToken = default)
    {
        await _writer.Write(ResponseType.HANDLE, cancellationToken).ConfigureAwait(false);
        await _writer.Write(requestId, cancellationToken).ConfigureAwait(false);
        await _writer.Write(handle, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendStat(uint requestId, SFTPAttributes attributes, CancellationToken cancellationToken = default)
    {
        await _writer.Write(ResponseType.ATTRS, cancellationToken).ConfigureAwait(false);
        await _writer.Write(requestId, cancellationToken).ConfigureAwait(false);
        await _writer.Write(attributes, PFlags.DEFAULT, cancellationToken).ConfigureAwait(false);
    }

    private Task SendStatus(uint requestId, Status status, CancellationToken cancellationToken = default)
        => SendStatus(requestId, status, GetStatusString(status), string.Empty, cancellationToken);

    private async Task SendStatus(uint requestId, Status status, string errorMessage, string languageTag, CancellationToken cancellationToken = default)
    {
        await _writer.Write(ResponseType.STATUS, cancellationToken).ConfigureAwait(false);
        await _writer.Write(requestId, cancellationToken).ConfigureAwait(false);
        await _writer.Write(status, cancellationToken).ConfigureAwait(false);
        if (_protocolversion > 2)
        {
            await _writer.Write(errorMessage, cancellationToken).ConfigureAwait(false);
            await _writer.Write(languageTag, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetStatusString(Status status)
        => status switch
        {
            Status.OK => "Succes",
            Status.EOF => "End of file",
            Status.NO_SUCH_FILE => "No such file",
            Status.PERMISSION_DENIED => "Permission denied",
            Status.FAILURE => "Failure",
            Status.BAD_MESSAGE => "Bad message",
            Status.NO_CONNECTION => "No connection",
            Status.CONNECTION_LOST => "Connection lost",
            Status.OP_UNSUPPORTED => "Operation unsupported",
            _ => "Unknown error"
        };
}