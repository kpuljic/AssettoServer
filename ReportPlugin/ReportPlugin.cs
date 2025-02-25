﻿using System.Collections.Concurrent;
using System.Drawing;
using System.Text.RegularExpressions;
using AssettoServer.Commands;
using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.GeoParams;
using AssettoServer.Server.Plugin;
using AssettoServer.Utils;
using CSharpDiscordWebhook.NET.Discord;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ReportPlugin;

public class ReportPlugin : CriticalBackgroundService, IAssettoServerAutostart
{
    private static readonly string[] SensitiveCharacters = { "\\", "*", "_", "~", "`", "|", ">", ":", "@" };
    // https://discord.com/developers/docs/resources/webhook#create-webhook
    private static readonly string[] ForbiddenUsernameSubstrings = { "clyde", "discord", "@", "#", ":", "```" };
    private static readonly string[] ForbiddenUsernames = { "everyone", "here" };
    
    internal Guid Key { get; }
    
    private readonly ReportConfiguration _configuration;
    private readonly DiscordWebhook? _webhook;
    private readonly string _serverNameTruncated;
    private readonly EntryCarManager _entryCarManager;
    private readonly CSPServerExtraOptions _cspServerExtraOptions;
    private readonly GeoParamsManager _geoParamsManager;
    private readonly ACServerConfiguration _serverConfiguration;
    private readonly Dictionary<ACTcpClient, Replay> _reports = new();
    private readonly ConcurrentQueue<AuditEvent> _events = new();

    public ReportPlugin(
        ReportConfiguration configuration,
        EntryCarManager entryCarManager,
        ChatService chatService,
        CSPServerExtraOptions cspServerExtraOptions,
        ACServerConfiguration serverConfiguration,
        GeoParamsManager geoParamsManager,
        IHostApplicationLifetime applicationLifetime) : base(applicationLifetime)
    {
        _configuration = configuration;
        _entryCarManager = entryCarManager;
        _cspServerExtraOptions = cspServerExtraOptions;
        _serverConfiguration = serverConfiguration;
        _geoParamsManager = geoParamsManager;

        _entryCarManager.ClientConnected += (sender, _) =>  sender.FirstUpdateSent += OnClientFirstUpdateSent;
        _entryCarManager.ClientDisconnected += OnClientDisconnected;
        chatService.MessageReceived += OnChatMessage;

        _serverNameTruncated = SanitizeUsername(serverConfiguration.Server.Name);

        if (!string.IsNullOrEmpty(_configuration.WebhookUrl))
        {
            _webhook = new DiscordWebhook
            {
                Uri = new Uri(_configuration.WebhookUrl)
            };
        }
        
        Key = Guid.NewGuid();
        Directory.CreateDirectory("reports");
    }
    
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // can't do this in constructor because geo params won't be initialized yet
        string extraOptions = $"\n[REPLAY_CLIPS]\nUPLOAD_URL = 'http://{_geoParamsManager.GeoParams.Ip}:{_serverConfiguration.Server.HttpPort}/report?key={Key}'\nDURATION = {_configuration.ClipDurationSeconds}";
        _cspServerExtraOptions.ExtraOptions += extraOptions;
        return Task.CompletedTask;
    }

    private void OnClientFirstUpdateSent(ACTcpClient sender, EventArgs args)
    {
        try
        {
            var auditEvent = new PlayerConnectedAuditEvent(new AuditClient(sender.EntryCar));
            _events.Enqueue(auditEvent);
            DeleteOldEvents();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error enqueueing audit event");
        }
    }

    private void OnClientDisconnected(ACTcpClient sender, EventArgs args)
    {
        try
        {
            var auditEvent = new PlayerDisconnectedAuditEvent(new AuditClient(sender));
            _events.Enqueue(auditEvent);
            DeleteOldEvents();

            _reports.Remove(sender);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error enqueueing audit event");
        }
    }

    private void OnChatMessage(ACTcpClient sender, ChatEventArgs args)
    {
        try
        {
            var auditEvent = new ChatMessageAuditEvent(new AuditClient(sender), args.Message);
            _events.Enqueue(auditEvent);
            DeleteOldEvents();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error enqueueing audit event");
        }
    }

    private void DeleteOldEvents()
    {
        while (_events.TryPeek(out var auditEvent))
        {
            if (auditEvent.Timestamp < DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(_configuration.ClipDurationSeconds)))
            {
                _events.TryDequeue(out _);
            }
            else
            {
                break;
            }
        }
    }
    
    private static string Sanitize(string? text)
    {
        text ??= "";
        
        foreach (string unsafeChar in SensitiveCharacters)
            text = text.Replace(unsafeChar, $"\\{unsafeChar}");
        return text;
    }
    
    private static string SanitizeUsername(string? name)
    {
        name ??= "";

        foreach (string str in ForbiddenUsernames)
        {
            if (name == str) return $"_{str}";
        }

        foreach (string str in ForbiddenUsernameSubstrings)
        {
            name = Regex.Replace(name, str, new string('*', str.Length), RegexOptions.IgnoreCase);
        }

        name = name.Substring(0, Math.Min(name.Length, 80));

        return name;
    }

    internal AuditLog GetAuditLog(DateTime timestamp)
    {
        DeleteOldEvents();
        var entryList = _entryCarManager.EntryCars.Select(car => new AuditClient(car));
        return new AuditLog(timestamp, entryList, _events.ToList());
    }

    internal async Task SubmitReport(ACTcpClient client, Replay replay, string reason)
    {
        if (_webhook == null)
            return;
        
        var msg = new DiscordMessage
        {
            Username = _serverNameTruncated,
            Embeds = new List<DiscordEmbed>
            {
                new DiscordEmbed
                {
                    Author = new EmbedAuthor
                    {
                        Name = Sanitize(client.Name),
                        Url = $"https://steamcommunity.com/profiles/{client.Guid}"
                    },
                    Color = Color.Red,
                    Description = Sanitize(reason),
                    Footer = new EmbedFooter
                    {
                        Text = "AssettoServer"
                    },
                    Timestamp = replay.AuditLog.Timestamp,
                    Title = "Report received"
                }
            },
            AllowedMentions = new AllowedMentions()
        };

        await _webhook.SendAsync(msg, new FileInfo(Path.Join("reports", $"{replay.Guid}.zip")), new FileInfo(Path.Join("reports", $"{replay.Guid}.json")));
    }
    
    public Replay? GetLastReplay(ACTcpClient client)
    {
        _reports.TryGetValue(client, out var report);
        return report;
    }

    public void SetLastReplay(ACTcpClient client, Replay replay) => _reports[client] = replay;
}
