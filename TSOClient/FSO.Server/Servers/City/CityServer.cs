﻿using FSO.Common.Domain.Shards;
using FSO.Server.Database.DA;
using FSO.Server.Database.DA.AvatarClaims;
using FSO.Server.Framework;
using FSO.Server.Framework.Aries;
using FSO.Server.Framework.Voltron;
using FSO.Server.Protocol.Aries.Packets;
using FSO.Server.Protocol.Voltron.Packets;
using FSO.Server.Servers.City.Domain;
using FSO.Server.Servers.City.Handlers;
using Ninject;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FSO.Server.Servers.City
{
    public class CityServer : AbstractAriesServer
    {
        private static Logger LOG = LogManager.GetCurrentClassLogger();
        private CityServerConfiguration Config;
        private ISessionGroup VoltronSessions;

        public CityServer(CityServerConfiguration config, IKernel kernel) : base(config, kernel)
        {
            this.Config = config;
            VoltronSessions = Sessions.GetOrCreateGroup(Groups.VOLTRON);
        }

        public override void Start()
        {
            LOG.Info("Starting city server for city: " + Config.ID);
            base.Start();
        }

        protected override void Bootstrap()
        {
            var shards = Kernel.Get<IShardsDomain>();
            var shard = shards.GetById(Config.ID);
            if (shard == null)
            {
                throw new Exception("Unable to find a shard with id " + Config.ID + ", check it exists in the database");
            }

            LOG.Info("City identified as " + shard.Name);

            var context = new CityServerContext();
            context.ShardId = shard.Id;
            context.Config = Config;
            Kernel.Bind<CityServerContext>().ToConstant(context);
            Kernel.Bind<int>().ToConstant(shard.Id).Named("ShardId");
            Kernel.Bind<CityServerConfiguration>().ToConstant(Config);
            Kernel.Bind<LotServerPicker>().To<LotServerPicker>().InSingletonScope();

            IDAFactory da = Kernel.Get<IDAFactory>();
            using (var db = da.Get()){
                var oldClaims = db.LotClaims.GetAllByOwner(context.Config.Call_Sign).ToList();
                if(oldClaims.Count > 0)
                {
                    LOG.Warn("Detected " + oldClaims.Count + " previously allocated lot claims, perhaps the server did not shut down cleanly. Lot consistency may be affected.");
                    db.LotClaims.RemoveAllByOwner(context.Config.Call_Sign);
                }

                var oldAvatarClaims = db.AvatarClaims.GetAllByOwner(context.Config.Call_Sign).ToList();
                if(oldAvatarClaims.Count > 0)
                {
                    LOG.Warn("Detected " + oldAvatarClaims.Count + " avatar claims, perhaps the server did not shut down cleanly. Avatar consistency may be affected.");
                    db.AvatarClaims.DeleteAll(context.Config.Call_Sign);
                }
            }

            base.Bootstrap();
        }

        protected override void HandleVoltronSessionResponse(IAriesSession session, object message)
        {
            var rawSession = (AriesSession)session;
            var packet = message as RequestClientSessionResponse;

            if (message != null)
            {
                using (var da = DAFactory.Get())
                {
                    var ticket = da.Shards.GetTicket(packet.Password);
                    if (ticket != null)
                    {
                        //TODO: Check if its expired
                        da.Shards.DeleteTicket(packet.Password);

                        //We need to lock this avatar
                        var claim = da.AvatarClaims.TryCreate(new DbAvatarClaim {
                            avatar_id = ticket.avatar_id,
                            location = 0,
                            owner = Config.Call_Sign
                        });

                        if (!claim.HasValue)
                        {
                            //Try and disconnect this user, if we still can't get a claim out of luck
                            var existingSession = Sessions.GetByAvatarId(ticket.avatar_id);
                            if(existingSession != null){
                                existingSession.Close();
                            }

                            //TODO: Broadcast to lot servers to disconnect
                            int i = 0;
                            while(i < 10)
                            {
                                claim = da.AvatarClaims.TryCreate(new DbAvatarClaim
                                {
                                    avatar_id = ticket.avatar_id,
                                    location = 0,
                                    owner = Config.Call_Sign
                                });

                                if (claim.HasValue){
                                    break;
                                }

                                Thread.Sleep(1000);
                            }

                            if (!claim.HasValue)
                            {
                                //No luck
                                session.Close();
                                return;
                            }
                        }

                        //Time to upgrade to a voltron session
                        var newSession = Sessions.UpgradeSession<VoltronSession>(rawSession, x => {
                            x.UserId = ticket.user_id;
                            x.AvatarId = ticket.avatar_id;
                            x.IsAuthenticated = true;
                            x.AvatarClaimId = claim.Value;
                        });
                        return;
                    }
                }
            }

            //Failed authentication
            rawSession.Close();
        }

        public override Type[] GetHandlers()
        {
            return new Type[]{
                typeof(SetPreferencesHandler),
                typeof(RegistrationHandler),
                typeof(DataServiceWrapperHandler),
                typeof(DBRequestWrapperHandler),
                typeof(VoltronConnectionLifecycleHandler),
                typeof(FindPlayerHandler),
                typeof(PurchaseLotHandler),
                typeof(LotServerAuthenticationHandler),
                typeof(LotServerLifecycleHandler),
                typeof(MessagingHandler),
                typeof(JoinLotHandler),
            };
        }
    }
}