public static class CandyAdminPage
{
    public const string Html = """
        <!doctype html><html lang="th"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
        <meta name="referrer" content="no-referrer"><title>CandyLive · สิทธิ์ลูกค้า</title>
        <style>
        *{box-sizing:border-box}body{background:#fff4db;color:#29243b;font:16px system-ui;margin:0}
        main{max-width:900px;margin:48px auto;padding:24px}h1{margin-bottom:4px}p{line-height:1.7}
        section{background:#fffdf5;border:2px solid #29243b;border-radius:20px;padding:24px;margin:24px 0}
        label{display:block;margin:16px 0 6px;font-weight:600}input{font:inherit;width:100%;padding:12px;border:1px solid #81738f;border-radius:10px}
        button{font:inherit;font-weight:600;border:2px solid #29243b;border-radius:12px;padding:12px 20px;background:#f35d99;color:#29243b;margin:14px 8px 0 0;cursor:pointer}
        button.secondary{background:#cbb5ef}button:disabled{opacity:.5}#status{min-height:32px;white-space:pre-wrap}
        article{padding:14px 0;border-top:1px solid #d4c7bb;overflow-wrap:anywhere}small{display:block;margin-top:6px}
        </style><main><h1>CandyLive</h1><p>จัดการสิทธิ์เล่นด้วย Discord ID · แยกจาก FlappyLive และ CookieLive</p>
        <section><label for="key">กุญแจผู้ดูแลเซิร์ฟเวอร์เดิม (FLAPPY_ADMIN_KEY)</label>
        <input id="key" type="password" autocomplete="off"><button id="load" class="secondary">ดูรายชื่อลูกค้า</button>
        <small>ใช้เฉพาะหน้านี้ ไม่บันทึกกุญแจลงเบราว์เซอร์</small></section>
        <section><h2>เพิ่มหรือแก้ไขสิทธิ์</h2><label for="id">Discord ID ของลูกค้า</label><input id="id" inputmode="numeric" placeholder="เลข ID 17–20 หลัก">
        <label for="expiry">วันหมดอายุ (เว้นว่าง = ไม่กำหนดวันหมดอายุ)</label><input id="expiry" type="datetime-local">
        <label for="note">หมายเหตุ / เลขคำสั่งซื้อ</label><input id="note" maxlength="300">
        <button id="grant">บันทึกสิทธิ์เล่น</button><button id="revoke" class="secondary">ถอนสิทธิ์</button>
        <small>ขณะออนไลน์ เกมตรวจสิทธิ์ประมาณทุก 2 นาที · หากติดต่อเซิร์ฟเวอร์ไม่ได้ สิทธิ์สำรองเดิมอาจใช้ได้สูงสุด 24 ชั่วโมง และไม่เกินวันหมดอายุที่ซื้อไว้</small></section>
        <p id="status" role="status"></p><section><h2>ลูกค้าล่าสุด (สูงสุด 500 บัญชี)</h2><div id="list"></div></section>
        </main><script>
        const $=id=>document.getElementById(id);let busy=false;
        async function api(path,body){
          if(!$('key').value)throw Error('กรอกกุญแจผู้ดูแลก่อน');
          const response=await fetch('/api/v1/candylive/admin/'+path,{method:body?'POST':'GET',
          headers:{'X-Admin-Key':$('key').value,'Content-Type':'application/json'},body:body?JSON.stringify(body):undefined,cache:'no-store'});
          if(!response.ok)throw Error(response.status===401?'กุญแจผู้ดูแลไม่ถูกต้อง':response.status===400?'ตรวจ Discord ID และวันหมดอายุอีกครั้ง':'เซิร์ฟเวอร์ยังไม่พร้อม ลองใหม่');
          return response.json();
        }
        async function load(){
          const rows=await api('licenses');$('list').replaceChildren();
          for(const r of rows){const el=document.createElement('article');el.textContent=r.discordId+' · '+(!r.active?'ถอนสิทธิ์แล้ว':r.expiresAt&&new Date(r.expiresAt)<=new Date()?'หมดอายุ':'มีสิทธิ์เล่น');
          const detail=document.createElement('small');detail.textContent=(r.expiresAt?'หมดอายุ '+new Date(r.expiresAt).toLocaleString('th-TH'):'ไม่กำหนดวันหมดอายุ')+' · '+r.note;
          const edit=document.createElement('button');edit.className='secondary';edit.textContent='เลือกบัญชีนี้';edit.onclick=()=>{$('id').value=r.discordId;$('note').value=r.note;$('expiry').value=r.expiresAt?localDate(new Date(r.expiresAt)):'';};
          el.append(detail,edit);$('list').append(el);}
        }
        function localDate(d){return new Date(d-d.getTimezoneOffset()*60000).toISOString().slice(0,16)}
        async function run(action){if(busy)return;busy=true;$('status').textContent='กำลังดำเนินการ…';
          for(const id of ['load','grant','revoke'])$(id).disabled=true;
          try{await action();$('status').textContent='เรียบร้อยแล้ว'}catch(e){$('status').textContent=e.message}
          finally{busy=false;for(const id of ['load','grant','revoke'])$(id).disabled=false}}
        async function save(active){
          const id=$('id').value.trim();if(!/^[0-9]{17,20}$/.test(id))throw Error('Discord ID ต้องเป็นตัวเลข 17–20 หลัก');
          if(!confirm((active?'ให้สิทธิ์เล่น CandyLive แก่ ':'ถอนสิทธิ์ CandyLive ของ ')+id+' ?'))return;
          await api('licenses',{discordId:id,active,expiresAt:$('expiry').value?new Date($('expiry').value).toISOString():null,note:$('note').value});await load();
        }
        $('load').onclick=()=>run(load);$('grant').onclick=()=>run(()=>save(true));$('revoke').onclick=()=>run(()=>save(false));
        </script></html>
        """;
}
