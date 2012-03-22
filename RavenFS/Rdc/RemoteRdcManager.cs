﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RavenFS.Client;
using RavenFS.Rdc.Wrapper;
using RavenFS.Infrastructure;

namespace RavenFS.Rdc
{
    public class RemoteRdcManager
    {
        private readonly ISignatureRepository _localSignatureRepository;
        private readonly ISignatureRepository _remoteCacheSignatureRepository;
        private readonly RavenFileSystemClient _ravenFileSystemClient;

        public RemoteRdcManager(RavenFileSystemClient ravenFileSystemClient, ISignatureRepository localSignatureRepository, ISignatureRepository remoteCacheSignatureRepository)
        {
            _localSignatureRepository = localSignatureRepository;
            _remoteCacheSignatureRepository = remoteCacheSignatureRepository;
            _ravenFileSystemClient = ravenFileSystemClient;
        }

        /// <summary>
        /// Returns signature manifest and synchronizes remote cache sig repository
        /// </summary>
        /// <param name="dataInfo"></param>
        /// <returns></returns>
        public Task<SignatureManifest> SynchronizeSignaturesAsync(DataInfo dataInfo)
        {
        	return _ravenFileSystemClient.Synchronization.GetRdcManifestAsync(dataInfo.Name)
            	.ContinueWith(task =>
            	{
            		var remoteSignatureManifest1 = task.Result;
            		if (remoteSignatureManifest1.Signatures.Count > 0)
            		{
            			return InternalSynchronizeSignaturesAsync(dataInfo, remoteSignatureManifest1)
            				.ContinueWith(task1 =>
            				{
            					task1.AssertNotFaulted();
            					return remoteSignatureManifest1;
            				});
            		}
            		return (Task<SignatureManifest>) new CompletedTask<SignatureManifest>(remoteSignatureManifest1);
            	}).Unwrap();
        }

        private Task InternalSynchronizeSignaturesAsync(DataInfo dataInfo, SignatureManifest remoteSignatureManifest)
        {
        	var sigPairs = PrepareSigPairs(dataInfo, remoteSignatureManifest);

        	var highestSigName = sigPairs.First().Remote;
        	var highestSigContent = _remoteCacheSignatureRepository.CreateContent(highestSigName);
        	return _ravenFileSystemClient.DownloadSignatureAsync(highestSigName, highestSigContent)
        		.ContinueWith(task =>
        		{
					for (var i = 1; i < sigPairs.Count(); i++)
					{
						var curr = sigPairs[i];
						var prev = sigPairs[i - 1];
						Synchronize(curr.Local, prev.Local, curr.Remote, prev.Remote).Wait();
					}
        		})
        		.ContinueWith(task =>
        		{
        			highestSigContent.Dispose();
        			return task;
        		}).Unwrap();
        	
        }

    	private class LocalRemotePair
        {
            public string Local { get; set; }
            public string Remote { get; set; }
        }

        private IList<LocalRemotePair> PrepareSigPairs(DataInfo dataInfo, SignatureManifest signatureManifest)
        {
            var remoteSignatures = signatureManifest.Signatures;
            var localSignatures = _localSignatureRepository.GetByFileName(dataInfo.Name).ToList();

            var length = Math.Min(remoteSignatures.Count, localSignatures.Count);
            var remoteSignatureNames = remoteSignatures.Take(length).Select(item => item.Name).ToList();
            var localSignatureNames = localSignatures.Take(length).Select(item => item.Name).ToList();
            return
                localSignatureNames.Zip(remoteSignatureNames,
                                        (local, remote) => new LocalRemotePair { Local = local, Remote = remote }).ToList();
        }

        private Task Synchronize(string localSigName, string localSigSigName, string remoteSigName, string remoteSigSigName)
        {
            using (var needListGenerator = new NeedListGenerator(_localSignatureRepository, _remoteCacheSignatureRepository))            
            {
                var source = new RemoteSignaturePartialAccess(_ravenFileSystemClient, remoteSigName);
                var seed = new SignaturePartialAccess(localSigName, _localSignatureRepository);
                var needList = needListGenerator.CreateNeedsList(new SignatureInfo(localSigSigName),
                                                                  new SignatureInfo(remoteSigSigName));
                var output = _remoteCacheSignatureRepository.CreateContent(remoteSigName);
                return NeedListParser.ParseAsync(source, seed, output, needList)
					.ContinueWith( task =>
					{
						output.Close();
						return task;
					}).Unwrap();
                
            }
        }
    }
}