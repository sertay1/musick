import { useCallback, useEffect, useRef, useState } from "react";
import type { NowPlayingState } from "../types/models";
import type React from "react";
import { api } from "../services/api";
export function usePlayerProgress(state:NowPlayingState|null,song:{spotifyUri:string;durationMs:number}|null,run:(action:()=>unknown)=>Promise<boolean>) {
    const [localProgress,setLocalProgress]=useState(state?.progressMs??0),[isSeeking,setIsSeeking]=useState(false),[seekValue,setSeekValue]=useState(0);
    const dragging=useRef(false),seekTrack=useRef<string|null>(null),quietUntil=useRef(0),progress=useRef(localProgress);
    const latest=useRef({uri:song?.spotifyUri,position:state?.progressMs??0}); latest.current={uri:song?.spotifyUri,position:state?.progressMs??0}; progress.current=localProgress;
    const duration=song?.durationMs??0;
    useEffect(()=>{
        if(dragging.current)return;
        const remote=state?.progressMs??0;
        if(Date.now()<quietUntil.current && Math.abs(remote-progress.current)>1500)return;
        quietUntil.current=0;setLocalProgress(remote);
    },[state?.progressMs,state?.isPlaying,isSeeking]);
    useEffect(()=>{dragging.current=false;seekTrack.current=null;quietUntil.current=0;setIsSeeking(false);setLocalProgress(latest.current.position);},[song?.spotifyUri]);
    useEffect(()=>{
        if(!state?.isPlaying||isSeeking)return;
        const timer=setInterval(()=>setLocalProgress(p=>duration?Math.min(duration,p+500):p+500),500);
        return ()=>clearInterval(timer);
    },[state?.isPlaying,isSeeking,song?.spotifyUri,duration]);
    const handleSeekChange=useCallback((e:React.ChangeEvent<HTMLInputElement>)=>{
        if(!dragging.current)seekTrack.current=latest.current.uri??null;
        dragging.current=true;setIsSeeking(true);setSeekValue(Number(e.currentTarget.value));
    },[]);
    const handleSeekCancel=useCallback(()=>{dragging.current=false;setIsSeeking(false);setLocalProgress(latest.current.position);},[]);
    const handleSeekCommit=useCallback((e:React.SyntheticEvent<HTMLInputElement>)=>{
        if(!dragging.current)return;
        const uri=seekTrack.current;dragging.current=false;setIsSeeking(false);
        if(!uri||uri!==latest.current.uri){setLocalProgress(latest.current.position);return;}
        const position=Math.max(0,Math.min(Number(e.currentTarget.value),duration||Number.MAX_SAFE_INTEGER));
        quietUntil.current=Date.now()+5000;setLocalProgress(position);
        void run(()=>api.seek(position,uri)).then(ok=>{if(!ok&&uri===latest.current.uri){quietUntil.current=0;setLocalProgress(latest.current.position);}});
    },[duration,run]);
    const handleSeekKeyUp=useCallback((e:React.KeyboardEvent<HTMLInputElement>)=>{
        if(["ArrowLeft","ArrowRight","ArrowUp","ArrowDown","Home","End","PageUp","PageDown"].includes(e.key))handleSeekCommit(e);
    },[handleSeekCommit]);
    return {localProgress,isSeeking,seekValue,handleSeekChange,handleSeekCommit,handleSeekCancel,handleSeekKeyUp};
}
