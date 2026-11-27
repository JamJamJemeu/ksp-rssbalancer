const {apSvgText}=require('./lib.js');
const fs=require('fs');
const C=120,F="#e8e5d8";
const p=(r,a)=>[(C+r*Math.sin(a)).toFixed(2),(C-r*Math.cos(a)).toFixed(2)];
const A0=-150*Math.PI/180, SW=300*Math.PI/180;
function face(knots){
  let s='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 240 240" width="480" height="480"><defs>'+
  '<radialGradient id="vemBz" cx=".35" cy=".3" r=".8"><stop offset="0" stop-color="#3a3c3d"/><stop offset=".5" stop-color="#232425"/><stop offset="1" stop-color="#0b0c0d"/></radialGradient>'+
  '<radialGradient id="vemFace" cx=".5" cy=".5" r=".5"><stop offset="0" stop-color="#121313"/><stop offset=".85" stop-color="#0e0f0f"/><stop offset="1" stop-color="#030303"/></radialGradient>'+
  '<radialGradient id="vemBr" cx=".38" cy=".32" r=".7"><stop offset="0" stop-color="#f3dc93"/><stop offset=".45" stop-color="#b48a38"/><stop offset="1" stop-color="#4f3a12"/></radialGradient>'+
  '<linearGradient id="vemCase" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#3a3c3d"/><stop offset=".5" stop-color="#232425"/><stop offset="1" stop-color="#0b0c0d"/></linearGradient>'+
  '</defs><rect x="0" y="0" width="240" height="240" rx="9" fill="url(#vemCase)"/>'+
  '<rect x="1" y="1" width="238" height="238" rx="8.5" fill="none" stroke="#fff" stroke-opacity=".07" stroke-width="1"/>';
  [[16,16,30],[224,16,70],[16,224,-20],[224,224,15]].forEach(q=>{s+='<g transform="translate('+q[0]+' '+q[1]+') rotate('+q[2]+')"><circle r="8.6" fill="#000" fill-opacity=".55" cx=".8" cy="1.3"/><circle r="8" fill="url(#vemBr)" stroke="#2a1d06" stroke-width=".8"/><rect x="-7" y="-1.2" width="14" height="2.4" rx=".6" fill="#2b1f08"/><rect x="-7" y="1.1" width="14" height=".6" fill="#f5e4a8" fill-opacity=".35"/></g>';});
  s+='<circle cx="120" cy="121.5" r="113" fill="#000" fill-opacity=".6"/><circle cx="120" cy="120" r="112" fill="url(#vemBz)"/><circle cx="120" cy="120" r="107.5" fill="#050505"/><circle cx="120" cy="120" r="105" fill="url(#vemFace)"/>';
  const max=knots?350:600, step=knots?5:10, mid=knots?25:50, maj=knots?50:100;
  for(let v=0;v<=max;v+=step){const a=A0+SW*v/max, M=v%maj===0, Md=v%mid===0;
    const r1=M?85:(Md?91:95),q1=p(r1,a),q2=p(101.5,a);
    s+='<line x1="'+q1[0]+'" y1="'+q1[1]+'" x2="'+q2[0]+'" y2="'+q2[1]+'" stroke="'+F+'" stroke-width="'+(M?3.4:(Md?2.2:1.5))+'"/>';}
  for(let v=0;v<=max;v+=maj){const a=A0+SW*v/max,q=p(72,a);s+=apSvgText(String(v/(knots?10:100)),+q[0],+q[1]+8.1,knots?19:22,F);}
  s+=apSvgText(knots?"×10 УЗЛЫ":"×100 КМ/Ч",121,79,9.5,F,"middle",0.4)+apSvgText("СКОРОСТЬ",120,95,11,F,"middle",0.7)+
     apSvgText(knots?"УС-350":"УС-600",120,180,7.5,"#8a877c","middle",0.4);
  // okienko licznika (4 bębenki) jak ВЭМ-72
  s+='<rect x="'+(120-38)+'" y="132" width="76" height="28" rx="1.5" fill="#000" stroke="'+F+'" stroke-width="1.5"/>';

  return s+'</svg>';
}
const needle='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 240 240" width="480" height="480">'+
 '<path d="M121.4 158 L118.6 158 L117.6 120 L118.3 64 L120 24 L121.7 64 L122.4 120Z" fill="#000" fill-opacity=".45" transform="translate(1.6 2.2)"/>'+
 '<path d="M121.4 158 L118.6 158 L117.6 120 L118.3 64 L120 24 L121.7 64 L122.4 120Z" fill="#e4e1d6"/>'+
 '<path d="M120 24 L121.7 64 L122.4 120 L121.4 158 L120 158Z" fill="#b9b6aa" fill-opacity=".3"/></svg>';
const top='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 240 240" width="480" height="480"><defs><linearGradient id="vemGl" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".22"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></linearGradient></defs>'+
 '<circle cx="121" cy="121.6" r="7" fill="#000" fill-opacity=".5"/><circle cx="120" cy="120" r="6.6" fill="#141414" stroke="#3b3b3b" stroke-width="1"/><circle cx="120" cy="120" r="2" fill="#5a5a58"/>'+
 '<ellipse cx="100" cy="74" rx="70" ry="34" transform="rotate(-30 120 120)" fill="url(#vemGl)"/><circle cx="120" cy="120" r="105" fill="none" stroke="#000" stroke-opacity=".7" stroke-width="3"/></svg>';
fs.writeFileSync('face_kmh.svg',face(false));fs.writeFileSync('face_kt.svg',face(true));fs.writeFileSync('needle.svg',needle);fs.writeFileSync('top.svg',top);
