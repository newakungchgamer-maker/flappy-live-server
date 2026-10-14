using System;
#nullable disable
using System.Collections.Generic;

namespace CookieDash
{
    [Serializable] public sealed class CookieCloudPhoto {public string viewerId,png;}
    [Serializable] public sealed class CookieCloudPhotoRequest
    {public string deviceId;public List<string> ids=new List<string>();public List<CookieCloudPhoto> photos=new List<CookieCloudPhoto>();}
    [Serializable] public sealed class CookieCloudPhotoReply
    {public bool ok;public string ownerId;public List<CookieCloudPhoto> photos=new List<CookieCloudPhoto>();}
    [Serializable] public sealed class CookieCareer
    {
        public string viewerId,name,courseKey;
        public long matches,finishes,wins,podiums,lastPlayedUtc;
        public float bestSeconds=-1;
    }
    [Serializable] public sealed class CookiePastWinner
    {public string viewerId,name;public int rank;public float seconds;}
    [Serializable] public sealed class CookiePastMatch
    {
        public string matchId,courseKey,courseLabel;
        public long endedUtc;
        public int runners,finished;
        public bool timedOut;
        public List<CookiePastWinner> podium=new List<CookiePastWinner>();
    }
    [Serializable] public sealed class CookieHistoryData
    {
        public int schema=1;
        public long completedMatches;
        public List<CookieCareer> players=new List<CookieCareer>();
        public List<CookiePastMatch> recent=new List<CookiePastMatch>();
        public List<string> appliedIds=new List<string>();
    }
    // Shared with the server: no Unity dependencies.
    public static class CookieCloudRules
    {
        public const int MaxProfiles=20000,MaxRecent=200,MaxApplied=4096,MaxFileBytes=16*1024*1024;
        static bool TextOK(string text,int max)=>!string.IsNullOrWhiteSpace(text) && text.Length<=max;
        static bool TimeOK(float seconds)=>!float.IsNaN(seconds) && !float.IsInfinity(seconds) && seconds>=0 && seconds<86400000;
        public static bool Valid(CookieHistoryData d,out string error)
        {
            error="Invalid history data.";
            if(d==null || d.schema!=1 || d.completedMatches<0 || d.players==null || d.recent==null || d.appliedIds==null ||
               d.players.Count>MaxProfiles || d.recent.Count>MaxRecent || d.appliedIds.Count>MaxApplied)return false;
            var keys=new HashSet<string>(StringComparer.Ordinal);
            foreach(var p in d.players)
            {
                if(p==null || !TextOK(p.viewerId,64) || !TextOK(p.name,128) || !TextOK(p.courseKey,128) || !keys.Add(p.courseKey+"|"+p.viewerId) ||
                   p.matches<1 || p.matches>d.completedMatches || p.finishes<0 || p.finishes>p.matches || p.wins<0 || p.wins>p.podiums || p.podiums>p.finishes ||
                   p.lastPlayedUtc<0 || (p.finishes==0?p.bestSeconds!=-1:!TimeOK(p.bestSeconds)))return false;
            }
            keys.Clear();foreach(var id in d.appliedIds)if(!TextOK(id,64) || !keys.Add(id))return false;
            keys.Clear();foreach(var m in d.recent)
            {
                if(m==null || !TextOK(m.matchId,64) || !keys.Add(m.matchId) || !d.appliedIds.Contains(m.matchId) || !TextOK(m.courseKey,128) || !TextOK(m.courseLabel,128) ||
                   m.runners<1 || m.runners>100 || m.finished<0 || m.finished>m.runners || m.endedUtc<0 || m.podium==null || m.podium.Count!=Math.Min(3,m.finished))return false;
                var ids=new HashSet<string>(StringComparer.Ordinal);float previous=-1;
                for(int i=0;i<m.podium.Count;i++)
                {
                    var p=m.podium[i];if(p==null || p.rank!=i+1 || !TextOK(p.viewerId,64) || !TextOK(p.name,128) || !ids.Add(p.viewerId) || !TimeOK(p.seconds) || p.seconds<previous)return false;previous=p.seconds;
                }
            }
            if(d.recent.Count>d.completedMatches || d.appliedIds.Count>d.completedMatches)return false;
            error="";return true;
        }

        // delta contains one complete match, never cumulative totals.
        public static bool IsDelta(CookieHistoryData d)
        {
            if(!Valid(d,out _) || d.completedMatches!=1 || d.recent.Count!=1 || d.appliedIds.Count!=1 ||
               d.recent[0].matchId!=d.appliedIds[0] || d.players.Count!=d.recent[0].runners)return false;
            var m=d.recent[0];int finishes=0,wins=0,podiums=0;
            foreach(var p in d.players)
            {
                if(p.matches!=1 || p.courseKey!=m.courseKey || p.lastPlayedUtc!=m.endedUtc)return false;
                finishes+=(int)p.finishes;wins+=(int)p.wins;podiums+=(int)p.podiums;
                var winner=m.podium.Find(w=>w.viewerId==p.viewerId);
                if((winner!=null)!=(p.podiums==1) || (winner!=null && (winner.seconds!=p.bestSeconds || winner.name!=p.name || (winner.rank==1)!=(p.wins==1))))return false;
            }
            return finishes==m.finished && wins==(finishes>0?1:0) && podiums==Math.Min(3,finishes);
        }
        // Caller owns a fresh copy; database transaction / local atomic write makes this all-or-nothing.
        public static void Merge(CookieHistoryData target,CookieHistoryData delta)
        {
            if(!IsDelta(delta))throw new ArgumentException("Invalid match delta");
            if(target.appliedIds.Contains(delta.appliedIds[0]))return;
            checked
            {
                foreach(var p in delta.players)
                {
                    var old=target.players.Find(x=>x.courseKey==p.courseKey && x.viewerId==p.viewerId);
                    if(old==null)
                    {
                        if(target.players.Count>=MaxProfiles)throw new InvalidOperationException("History capacity reached");
                        old=new CookieCareer{viewerId=p.viewerId,courseKey=p.courseKey};target.players.Add(old);
                    }
                    old.matches+=p.matches;old.finishes+=p.finishes;old.wins+=p.wins;old.podiums+=p.podiums;
                    if(p.lastPlayedUtc>=old.lastPlayedUtc){old.name=p.name;old.lastPlayedUtc=p.lastPlayedUtc;}
                    if(p.bestSeconds>=0 && (old.bestSeconds<0 || p.bestSeconds<old.bestSeconds))old.bestSeconds=p.bestSeconds;
                }
                target.completedMatches++;
            }
            target.recent.Add(delta.recent[0]);target.recent.Sort((a,b)=>{int c=b.endedUtc.CompareTo(a.endedUtc);return c!=0?c:string.CompareOrdinal(a.matchId,b.matchId);});
            if(target.recent.Count>MaxRecent)target.recent.RemoveRange(MaxRecent,target.recent.Count-MaxRecent);
            target.appliedIds.Add(delta.appliedIds[0]);
            // Keep every visible recent match in the bounded client replay window, even after a very late offline upload.
            while(target.appliedIds.Count>MaxApplied)
            {
                int i=target.appliedIds.FindIndex(id=>!target.recent.Exists(m=>m.matchId==id));
                target.appliedIds.RemoveAt(i<0?0:i);
            }
            if(!Valid(target,out var error))throw new InvalidOperationException(error);
        }
    }
    [Serializable] public sealed class CookieCloudSyncRequest
    {
        public bool hasBootstrap;
        public string deviceId,bootstrapId;
        public CookieHistoryData bootstrap;
        public List<CookieHistoryData> matches=new List<CookieHistoryData>();
    }
    [Serializable] public sealed class CookieCloudSyncReply
    {
        public bool ok;
        public string ownerId,bootstrapState;
        public CookieHistoryData history;
        public List<string> acknowledged=new List<string>();
    }
    [Serializable] public sealed class CookieCloudLocalState
    {
        public bool hasBootstrap;
        public int schema=1;
        public string ownerId,bootstrapId;
        public CookieHistoryData baseline=new CookieHistoryData(),bootstrap;
        public List<CookieHistoryData> pending=new List<CookieHistoryData>();
        public string bootstrapState;
    }
}
