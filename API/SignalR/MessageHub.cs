using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using API.Interfaces;
using AutoMapper;
using Microsoft.AspNetCore.SignalR;
using API.Extensions;
using API.DTOs;
using API.Entities;

namespace API.SignalR
{
    public class MessageHub : Hub
    {
        private readonly IUnitOfWork _UnitOfWork;
        private readonly IMapper _mapper;
        private readonly IHubContext<PresenceHub> _presenceHub;
        private readonly PresenceTracker _tracker;
        public MessageHub(IUnitOfWork UnitOfWork,IMapper mapper,
            IHubContext<PresenceHub> presenceHub,
            PresenceTracker tracker)
        {
            _UnitOfWork = UnitOfWork;
            _mapper = mapper;
            _presenceHub = presenceHub;
            _tracker = tracker;
        }

        public override async Task OnConnectedAsync()
        {
            var httpContext = Context.GetHttpContext();
            var otherUser = httpContext.Request.Query["user"].ToString();
            string groupName = GetGroupName(Context.User.GetUsername(),otherUser);
            await Groups.AddToGroupAsync(Context.ConnectionId,groupName);
            var group = await AddToGroup(groupName);
            await Clients.Group(groupName).SendAsync("UpdatedGroup",group);

            var messages = await _UnitOfWork.MessageRepository.
                GetMessageThread(Context.User.GetUsername(),otherUser);

            if(_UnitOfWork.HasChanges()) await _UnitOfWork.Complete();

            await Clients.Caller.SendAsync("ReceiveMessageThread",messages);
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var group = await RemoveFromMessageGroup();
            await Clients.Group(group.Name).SendAsync("UpdatedGroup",group);
            await base.OnDisconnectedAsync(exception);
        }

        public async Task SendMessage(CreateMessageDto createMessageDto)
        {
            var username = Context.User.GetUsername();

            if(username == createMessageDto.RecipientUsername.ToLower())
                throw new HubException("You can not send message to yourself");

            var sender = await _UnitOfWork.UserRepository.GetUserByUsernameAsync(username);
            var recipient = await _UnitOfWork.UserRepository.GetUserByUsernameAsync(createMessageDto.RecipientUsername);
            if(recipient == null)  throw new HubException("Not found user");
            var message = new Message
            {
                Sender = sender,
                Recipient = recipient,
                SenderUsername = sender.UserName,
                RecipientUsername = recipient.UserName,
                Content = createMessageDto.Content
            };
            string groupName = GetGroupName(sender.UserName,recipient.UserName);

            var group = await _UnitOfWork.MessageRepository.GetMessageGroup(groupName);

            if(group.Connections.Any(a=>a.Username == recipient.UserName))
            {
                message.DateRead = DateTime.Now;
            }
            else
            {
                var connections = await _tracker.GetConnectionsForUser(recipient.UserName);
                if(connections != null)
                {
                    await _presenceHub.Clients.Clients(connections).SendAsync("NewMessageReceived",
                        new { username = sender.UserName, knownAs = sender.KnownAs});
                }
            }

            _UnitOfWork.MessageRepository.AddMessage(message);

            if(await _UnitOfWork.Complete())
            {
               await Clients.Groups(groupName).SendAsync("NewMessage",_mapper.Map<MessageDto>(message));
            } 
        }

        private async Task<Group> AddToGroup(string groupName)
        {
            var group = await _UnitOfWork.MessageRepository.GetMessageGroup(groupName);
            var connection = new Connection(Context.ConnectionId,Context.User.GetUsername());
            if(group == null)
            {
                group = new Group(groupName);
                _UnitOfWork.MessageRepository.AddGroup(group);
            }

            group.Connections.Add(connection);
            if(await _UnitOfWork.Complete()) return group;

            throw new HubException("Failed to join group");
        }

        private async Task<Group> RemoveFromMessageGroup()
        {
            var group = await _UnitOfWork.MessageRepository.GetGroupForConnection(Context.ConnectionId);
            var connection = group.Connections.FirstOrDefault(x=>x.ConnectionId == Context.ConnectionId);
            _UnitOfWork.MessageRepository.RemoveConnection(connection);
            if(await _UnitOfWork.Complete()) return group;

            throw new HubException("Failed to remove from group");
        } 

        private string GetGroupName(string caller,string other)
        {
            var stringCompare = string.CompareOrdinal(caller,other) < 0;
            return stringCompare ? $"{caller}-{other}" : $"{other}-{caller}";
        }
    }
}