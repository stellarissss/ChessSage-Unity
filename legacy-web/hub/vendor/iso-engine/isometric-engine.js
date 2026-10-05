const F = {
  top: "#6C9BCF",
  left: "#4A7AB0",
  right: "#3A6691"
}, es = {
  top: { r: 108, g: 155, b: 207 },
  left: { r: 74, g: 122, b: 176 },
  right: { r: 58, g: 102, b: 145 }
}, se = {
  width: 100,
  height: 100,
  depth: 50
}, ie = {
  x: 0,
  y: 0,
  z: 0
}, W = {
  rotateX: 60,
  // 俯视倾斜角（度）
  rotateZ: 45
  // 平面旋转角（度）
}, ss = W.rotateX, ne = 1e3, re = 1, Z = {
  rotateX: W.rotateX,
  rotateZ: W.rotateZ
};
function oe() {
  return Z.rotateZ;
}
function ae() {
  return Z.rotateX;
}
function xt() {
  const r = Z.rotateZ * Math.PI / 180, t = Z.rotateX * Math.PI / 180;
  return {
    cosZ: Math.cos(r),
    sinZ: Math.sin(r),
    cosX: Math.cos(t),
    sinX: Math.sin(t)
  };
}
function he(r, t) {
  Z.rotateX = r, Z.rotateZ = t;
}
const _t = W.rotateZ, Zt = W.rotateX, bt = _t * Math.PI / 180, Ut = Zt * Math.PI / 180, gt = Math.cos(bt), mt = Math.sin(bt), pt = Math.cos(Ut), zt = Math.sin(Ut), is = _t, ce = bt, le = gt, pe = mt;
function jt(r, t = 1, e = { x: 0, y: 0 }) {
  const { cosZ: s, sinZ: i, cosX: n, sinX: o } = xt(), h = (r.x - r.y) * s, a = (r.x + r.y) * i, c = h, l = a * n - r.z * o;
  return {
    x: c * t + e.x,
    y: l * t + e.y
  };
}
function de(r, t = 0, e = 1, s = { x: 0, y: 0 }) {
  const { cosZ: i, sinZ: n, cosX: o, sinX: h } = xt(), a = (r.x - s.x) / e, c = (r.y - s.y) / e + t * h, l = a / i, p = c / (n * o), m = (l + p) / 2, f = (p - l) / 2;
  return { x: m, y: f, z: t };
}
function fe(r, t = 0, e = 0, s = 0) {
  const i = r.x + e / 2, n = r.y + s / 2;
  return Math.round((i + n) * 10 + r.z * 1);
}
function Bt(r, t) {
  const e = t.x - r.x, s = t.y - r.y, i = t.z - r.z;
  return Math.sqrt(e * e + s * s + i * i);
}
function ns(r, t = 20, e = { x: 0, y: 0, z: 0 }) {
  return {
    x: e.x + r.row * t,
    y: e.y + r.col * t,
    z: e.z + (r.layer ?? 0) * t
  };
}
function rs(r, t = 20, e = { x: 0, y: 0, z: 0 }) {
  return {
    row: Math.round((r.x - e.x) / t),
    col: Math.round((r.y - e.y) / t),
    layer: Math.round((r.z - e.z) / t)
  };
}
function os(r, t, e = { x: 0, y: 0, z: 0 }) {
  const s = [];
  for (let i = 0; i < r; i++)
    s.push({
      x: e.x + i * t,
      y: e.y - i * t,
      z: e.z
    });
  return s;
}
function as(r, t, e = { x: 0, y: 0, z: 0 }) {
  const s = [];
  for (let i = 0; i < r; i++)
    s.push({
      x: e.x + i * t,
      y: e.y + i * t,
      z: e.z
    });
  return s;
}
function hs(r, t, e = { x: 0, y: 0, z: 0 }) {
  const s = [];
  for (let i = 0; i < r; i++)
    s.push({
      x: e.x,
      y: e.y,
      z: e.z + i * t
    });
  return s;
}
class ue {
  constructor(t = {}) {
    this.angle = t.angle !== void 0 ? t.angle * Math.PI / 180 : ce, this.perspective = t.perspective ?? ne, this.scale = t.scale ?? re, this.origin = t.origin ?? { x: 0, y: 0 };
  }
  /**
   * 将等距坐标转换为屏幕坐标
   */
  isoToScreen(t) {
    return jt(t, this.scale, this.origin);
  }
  /**
   * 将屏幕坐标转换为等距坐标 (假设 z = 0)
   */
  screenToIso(t, e = 0) {
    return de(t, e, this.scale, this.origin);
  }
  /**
   * 获取 CSS 3D Transform 字符串
   */
  getCSS3DTransform(t) {
    return `translate3d(${t.x}px, ${t.y}px, ${t.z}px)`;
  }
  /**
   * 获取等距平面的 CSS Transform
   */
  getIsometricPlaneTransform() {
    return `rotateX(60deg) rotateZ(${this.angle * 180 / Math.PI}deg)`;
  }
  /**
   * 获取透视容器的 CSS 样式
   */
  getPerspectiveStyle() {
    return {
      perspective: `${this.perspective}px`,
      perspectiveOrigin: "50% 50%",
      transformStyle: "preserve-3d"
    };
  }
  /**
   * 计算两个等距坐标之间的距离
   */
  distance(t, e) {
    return Bt(t, e);
  }
  /**
   * 计算 Z-Index 排序值
   */
  calculateZIndex(t) {
    return fe(t);
  }
  /**
   * 更新变换参数
   */
  update(t) {
    t.angle !== void 0 && (this.angle = t.angle * Math.PI / 180), t.perspective !== void 0 && (this.perspective = t.perspective), t.scale !== void 0 && (this.scale = t.scale), t.origin !== void 0 && (this.origin = t.origin);
  }
  /**
   * 获取当前配置
   */
  getConfig() {
    return {
      angle: this.angle * 180 / Math.PI,
      perspective: this.perspective,
      scale: this.scale,
      origin: { ...this.origin }
    };
  }
}
class ge {
  constructor(t, e = {}) {
    this.isoContainer = null, this.entities = /* @__PURE__ */ new Set(), this.container = t, this.options = {
      angle: e.angle ?? 45,
      perspective: e.perspective ?? 1e3,
      scale: e.scale ?? 1,
      origin: e.origin ?? { x: 0, y: 0 }
    }, this.transform = new ue(this.options), this.wrapper = this.createWrapper(), this.container.appendChild(this.wrapper), this.applyContainerStyles();
  }
  /**
   * 创建场景内部包装器
   */
  createWrapper() {
    const t = document.createElement("div");
    return t.className = "isometric-scene-wrapper", Object.assign(t.style, {
      position: "absolute",
      left: "0",
      top: "0",
      width: "100%",
      height: "100%",
      overflow: "visible"
    }), this.isoContainer = document.createElement("div"), this.isoContainer.className = "isometric-container", Object.assign(this.isoContainer.style, {
      position: "absolute",
      left: "0",
      top: "0",
      width: "0",
      height: "0"
    }), t.appendChild(this.isoContainer), t;
  }
  /**
   * 应用容器样式
   */
  applyContainerStyles() {
    Object.assign(this.container.style, {
      position: "relative",
      overflow: "hidden"
    }), this.container.classList.add("isometric-scene");
  }
  /**
   * 添加实体到场景
   */
  add(t) {
    return this.entities.has(t) || (this.entities.add(t), t.attachToScene(this), this.isoContainer.appendChild(t.getElement()), this.updateEntityZIndex(t)), this;
  }
  /**
   * 从场景移除实体
   */
  remove(t) {
    if (this.entities.has(t)) {
      this.entities.delete(t), t.detachFromScene();
      const e = t.getElement();
      e.parentNode === this.isoContainer && this.isoContainer.removeChild(e);
    }
    return this;
  }
  /**
   * 更新实体的 z-index
   */
  updateEntityZIndex(t) {
    const e = t.getAbsolutePosition(), s = this.transform.calculateZIndex(e);
    t.getElement().style.zIndex = String(s);
  }
  /**
   * 更新所有实体的 z-index
   */
  updateAllZIndices() {
    this.entities.forEach((t) => {
      this.updateEntityZIndex(t);
    });
  }
  /**
   * 获取坐标变换器
   */
  getTransform() {
    return this.transform;
  }
  /**
   * 获取场景包装器元素
   */
  getWrapper() {
    return this.wrapper;
  }
  /**
   * 获取场景容器元素
   */
  getContainer() {
    return this.container;
  }
  /**
   * 将屏幕坐标转换为等距坐标
   */
  screenToIso(t, e = 0) {
    const s = this.container.getBoundingClientRect(), i = t.x - s.left - s.width / 2, n = t.y - s.top - s.height / 2;
    return this.transform.screenToIso({ x: i, y: n }, e);
  }
  /**
   * 将等距坐标转换为屏幕坐标
   */
  isoToScreen(t) {
    const e = this.transform.isoToScreen(t), s = this.container.getBoundingClientRect();
    return {
      x: e.x + s.left + s.width / 2,
      y: e.y + s.top + s.height / 2
    };
  }
  /**
   * 更新场景配置
   */
  update(t) {
    Object.assign(this.options, t), this.transform.update(t), this.entities.forEach((e) => {
      e.updateTransform(), this.updateEntityZIndex(e);
    });
  }
  /**
   * 获取场景中的所有实体
   */
  getEntities() {
    return Array.from(this.entities);
  }
  /**
   * 清空场景
   */
  clear() {
    this.entities.forEach((t) => {
      t.detachFromScene();
    }), this.entities.clear(), this.isoContainer.innerHTML = "";
  }
  /**
   * 销毁场景
   */
  destroy() {
    this.clear(), this.container.removeChild(this.wrapper), this.container.classList.remove("isometric-scene");
  }
  /**
   * 设置场景原点（通常设置为容器中心）
   */
  centerOrigin() {
    const t = this.container.getBoundingClientRect();
    this.update({
      origin: { x: t.width / 2, y: t.height / 2 }
    });
  }
}
class me {
  constructor(t, e, s, i, n) {
    this._defaultPrevented = !1, this._propagationStopped = !1, this.type = t, this.target = e, this.originalEvent = s, this.position = i, this.screenPosition = n;
  }
  preventDefault() {
    this._defaultPrevented = !0, this.originalEvent.preventDefault();
  }
  stopPropagation() {
    this._propagationStopped = !0, this.originalEvent.stopPropagation();
  }
  get defaultPrevented() {
    return this._defaultPrevented;
  }
  get propagationStopped() {
    return this._propagationStopped;
  }
}
class $t {
  constructor(t) {
    this.listeners = /* @__PURE__ */ new Map(), this.target = t;
  }
  /**
   * 添加事件监听器
   */
  on(t, e) {
    return this.listeners.has(t) || this.listeners.set(t, /* @__PURE__ */ new Set()), this.listeners.get(t).add(e), this;
  }
  /**
   * 移除事件监听器
   */
  off(t, e) {
    const s = this.listeners.get(t);
    return s && (s.delete(e), s.size === 0 && this.listeners.delete(t)), this;
  }
  /**
   * 添加一次性事件监听器
   */
  once(t, e) {
    const s = (i) => {
      this.off(t, s), e(i);
    };
    return this.on(t, s);
  }
  /**
   * 触发事件
   */
  emit(t, e, s, i) {
    const n = this.listeners.get(t);
    if (!n || n.size === 0) return;
    const o = new me(
      t,
      this.target,
      e,
      s,
      i
    );
    n.forEach((h) => {
      o.propagationStopped || h(o);
    });
  }
  /**
   * 检查是否有指定类型的监听器
   */
  hasListeners(t) {
    const e = this.listeners.get(t);
    return e !== void 0 && e.size > 0;
  }
  /**
   * 移除所有监听器
   */
  removeAllListeners(t) {
    return t ? this.listeners.delete(t) : this.listeners.clear(), this;
  }
  /**
   * 销毁事件分发器
   */
  destroy() {
    this.listeners.clear();
  }
}
const rt = /* @__PURE__ */ new Map();
function K(r) {
  const t = (rt.get(r) ?? 0) + 1;
  return rt.set(r, t), `${r}-${t}`;
}
function cs(r) {
  rt.delete(r);
}
function ls() {
  rt.clear();
}
function et(r) {
  if (!r.startsWith("#"))
    return { r: 255, g: 255, b: 255 };
  const t = r.slice(1);
  return t.length === 3 ? {
    r: parseInt(t[0] + t[0], 16),
    g: parseInt(t[1] + t[1], 16),
    b: parseInt(t[2] + t[2], 16)
  } : t.length === 6 ? {
    r: parseInt(t.slice(0, 2), 16),
    g: parseInt(t.slice(2, 4), 16),
    b: parseInt(t.slice(4, 6), 16)
  } : { r: 255, g: 255, b: 255 };
}
function st(r) {
  const t = Math.max(0, Math.min(255, Math.round(r.r))), e = Math.max(0, Math.min(255, Math.round(r.g))), s = Math.max(0, Math.min(255, Math.round(r.b)));
  return `#${t.toString(16).padStart(2, "0")}${e.toString(16).padStart(2, "0")}${s.toString(16).padStart(2, "0")}`;
}
function Tt(r, t) {
  if (!r.startsWith("#")) return r;
  const e = et(r);
  return st({
    r: e.r + t,
    g: e.g + t,
    b: e.b + t
  });
}
function ye(r, t) {
  return {
    r: Math.min(255, Math.round(r.r + t)),
    g: Math.min(255, Math.round(r.g + t)),
    b: Math.min(255, Math.round(r.b + t))
  };
}
function kt(r, t) {
  return {
    r: Math.max(0, Math.round(r.r - t)),
    g: Math.max(0, Math.round(r.g - t)),
    b: Math.max(0, Math.round(r.b - t))
  };
}
class ve {
  constructor(t, e, s) {
    this.container = t, this.size = e, this.colors = this.calculateColors(s);
  }
  /**
   * 计算三个面的颜色
   */
  calculateColors(t) {
    return t ? {
      top: t,
      left: Tt(t, -20),
      right: Tt(t, -40)
    } : { ...F };
  }
  /**
   * 创建立方体
   */
  render() {
    const { width: t, height: e, depth: s } = this.size, i = document.createElement("div");
    i.className = "cube", Object.assign(i.style, {
      position: "absolute",
      width: `${t}px`,
      height: `${e}px`,
      transformStyle: "preserve-3d",
      transform: `translate3d(-50%, -50%, 0) rotateX(${Zt}deg) rotateZ(${_t}deg)`,
      pointerEvents: "none"
    }), [
      {
        name: "top",
        width: t,
        height: e,
        color: this.colors.top,
        transform: `translateZ(${s}px)`,
        transformOrigin: "center"
      },
      {
        name: "left",
        width: t,
        height: s,
        color: this.colors.left,
        transform: `translateY(${e}px) rotateX(90deg)`,
        transformOrigin: "left top"
      },
      {
        name: "right",
        width: s,
        height: e,
        color: this.colors.right,
        transform: `translateX(${t}px) rotateY(-90deg)`,
        transformOrigin: "left top"
      }
    ].forEach((o) => {
      i.appendChild(this.createFace(o));
    }), this.container.appendChild(i);
  }
  /**
   * 创建单个面
   */
  createFace(t) {
    const e = document.createElement("div");
    return e.className = `isometric-face isometric-face-${t.name}`, Object.assign(e.style, {
      position: "absolute",
      width: `${t.width}px`,
      height: `${t.height}px`,
      background: t.color,
      transform: t.transform,
      transformOrigin: t.transformOrigin,
      backfaceVisibility: "visible",
      boxSizing: "border-box",
      pointerEvents: "auto"
    }), e;
  }
  /**
   * 更新尺寸
   */
  updateSize(t) {
    this.size = t, this.container.innerHTML = "", this.render();
  }
  /**
   * 更新颜色
   */
  updateColors(t) {
    this.colors = this.calculateColors(t);
    const e = this.container.querySelector(".isometric-face-top"), s = this.container.querySelector(".isometric-face-left"), i = this.container.querySelector(".isometric-face-right");
    e && (e.style.background = this.colors.top), s && (s.style.background = this.colors.left), i && (i.style.background = this.colors.right);
  }
  /**
   * 设置光照强度
   * @param intensity 光照强度 0-100
   */
  setLightIntensity(t) {
    const e = Math.max(0, Math.min(100, t)) / 100, s = et(F.top), i = et(F.left), n = et(F.right), o = (e - 0.5) * 80, h = ye(s, o), a = kt(i, o * 0.5), c = kt(n, o), l = this.container.querySelector(".isometric-face-top"), p = this.container.querySelector(".isometric-face-left"), m = this.container.querySelector(".isometric-face-right");
    l && (l.style.background = st(h)), p && (p.style.background = st(a)), m && (m.style.background = st(c));
  }
  /**
   * 获取指定面的元素
   */
  getFaceElement(t) {
    return this.container.querySelector(`.isometric-face-${t}`);
  }
  /**
   * 设置面的内容
   */
  setFaceContent(t, e) {
    const s = this.getFaceElement(t);
    s && (typeof e == "string" ? s.innerHTML = e : (s.innerHTML = "", s.appendChild(e)));
  }
}
class it {
  constructor(t = {}) {
    this.scene = null, this.parent = null, this.children = /* @__PURE__ */ new Set(), this.stackOffset = { x: 0, y: 0, z: 0 }, this.activeEffects = /* @__PURE__ */ new Set(), this.id = K("entity"), this.options = t, this.position = t.position ?? { ...ie }, this.size = t.size ?? { ...se }, this.element = this.createElement(), this.eventDispatcher = new $t(this);
    const e = t.style?.backgroundColor;
    this.cubeRenderer = new ve(this.element, this.size, e), this.cubeRenderer.render(), t.texture && this.setTexture(t.texture), t.style && Object.assign(this.element.style, t.style), t.className && this.element.classList.add(t.className), this.bindDOMEvents();
  }
  /**
   * 创建实体 DOM 元素
   */
  createElement() {
    const t = document.createElement("div");
    return t.className = "isometric-entity", t.dataset.entityId = this.id, Object.assign(t.style, {
      position: "absolute",
      cursor: "pointer",
      userSelect: "none",
      width: "0",
      height: "0",
      overflow: "visible",
      transformStyle: "preserve-3d"
    }), t;
  }
  /**
   * 更新 CSS 变换
   */
  updateTransform() {
    if (!this.scene) return;
    const t = this.scene.getTransform(), e = this.getAbsolutePosition(), s = t.isoToScreen(e);
    this.element.style.left = `${s.x}px`, this.element.style.top = `${s.y}px`, this.scene.updateEntityZIndex(this), this.children.forEach((i) => i.updateTransform());
  }
  /**
   * 绑定 DOM 事件
   */
  bindDOMEvents() {
    this.element.addEventListener("click", (e) => {
      this.emitEvent("click", e);
    }), this.element.addEventListener("mouseover", (e) => {
      this.element.contains(e.relatedTarget) || this.emitEvent("hover", e);
    }), this.element.addEventListener("mouseout", (e) => {
      this.element.contains(e.relatedTarget) || this.emitEvent("hoverEnd", e);
    });
    let t = !1;
    this.element.addEventListener("mousedown", (e) => {
      t = !0, this.emitEvent("dragStart", e);
    }), document.addEventListener("mousemove", (e) => {
      t && this.emitEvent("drag", e);
    }), document.addEventListener("mouseup", (e) => {
      t && (t = !1, this.emitEvent("dragEnd", e));
    });
  }
  /**
   * 触发事件
   */
  emitEvent(t, e) {
    const s = {
      x: e.clientX ?? 0,
      y: e.clientY ?? 0
    };
    this.eventDispatcher.emit(t, e, this.getAbsolutePosition(), s);
  }
  /**
   * 添加事件监听器
   */
  on(t, e) {
    return this.eventDispatcher.on(t, e), this;
  }
  /**
   * 移除事件监听器
   */
  off(t, e) {
    return this.eventDispatcher.off(t, e), this;
  }
  /**
   * 设置位置
   */
  setPosition(t) {
    return Object.assign(this.position, t), this.updateTransform(), this;
  }
  /**
   * 获取相对位置
   */
  getPosition() {
    return { ...this.position };
  }
  /**
   * 获取绝对位置（考虑父实体）
   */
  getAbsolutePosition() {
    if (this.parent) {
      const t = this.parent.getAbsolutePosition();
      return {
        x: t.x + this.position.x + this.stackOffset.x,
        y: t.y + this.position.y + this.stackOffset.y,
        z: t.z + this.position.z + this.stackOffset.z
      };
    }
    return { ...this.position };
  }
  /**
   * 设置尺寸
   */
  setSize(t) {
    return Object.assign(this.size, t), this.cubeRenderer.updateSize(this.size), this.updateTransform(), this;
  }
  /**
   * 获取尺寸
   */
  getSize() {
    return { ...this.size };
  }
  /**
   * 设置纹理
   */
  setTexture(t) {
    const e = this.cubeRenderer.getFaceElement("top");
    return e ? (typeof t == "string" ? t.startsWith("http") || t.startsWith("/") ? (e.style.backgroundImage = `url(${t})`, e.style.backgroundSize = "cover", e.style.backgroundPosition = "center") : e.innerHTML = t : (e.innerHTML = "", e.appendChild(t)), this) : this;
  }
  /**
   * 设置指定面的 innerHTML
   */
  setFaceInnerHTML(t, e) {
    return this.cubeRenderer.setFaceContent(t, e), this;
  }
  /**
   * 获取指定面的 DOM 元素
   */
  getFaceElement(t) {
    return this.cubeRenderer.getFaceElement(t);
  }
  /**
   * 设置光照强度
   */
  setLightIntensity(t) {
    return this.cubeRenderer.setLightIntensity(t), this;
  }
  /**
   * 附加到场景
   */
  attachToScene(t) {
    this.scene = t, this.updateTransform();
  }
  /**
   * 从场景分离
   */
  detachFromScene() {
    this.scene = null;
  }
  /**
   * 获取 DOM 元素
   */
  getElement() {
    return this.element;
  }
  /**
   * 堆叠到另一个实体上
   */
  stackOn(t, e = { x: 0, y: 0, z: 0 }) {
    return this.parent && this.parent.children.delete(this), this.parent = t, this.stackOffset = e, e.z === 0 && (this.stackOffset.z = t.size.depth), t.children.add(this), t.scene && !this.scene && t.scene.add(this), this.updateTransform(), this;
  }
  /**
   * 取消堆叠
   */
  unstack() {
    return this.parent && (this.parent.children.delete(this), this.parent = null, this.stackOffset = { x: 0, y: 0, z: 0 }, this.updateTransform()), this;
  }
  /**
   * 获取父实体
   */
  getParent() {
    return this.parent;
  }
  /**
   * 获取子实体
   */
  getChildren() {
    return Array.from(this.children);
  }
  /**
   * 应用特效
   */
  applyEffect(t) {
    const e = `isometric-effect-${t.type}`;
    this.element.classList.add(e), this.activeEffects.add(t.type);
    const s = t.duration ?? 1e3, i = t.intensity ?? 1;
    return this.element.style.setProperty("--effect-duration", `${s}ms`), this.element.style.setProperty("--effect-intensity", String(i)), t.color && this.element.style.setProperty("--effect-color", t.color), t.loop || setTimeout(() => {
      this.removeEffect(t.type);
    }, s), this;
  }
  /**
   * 移除特效
   */
  removeEffect(t) {
    const e = `isometric-effect-${t}`;
    return this.element.classList.remove(e), this.activeEffects.delete(t), this;
  }
  /**
   * 移除所有特效
   */
  clearEffects() {
    return this.activeEffects.forEach((t) => {
      this.element.classList.remove(`isometric-effect-${t}`);
    }), this.activeEffects.clear(), this;
  }
  /**
   * 设置可见性
   */
  setVisible(t) {
    return this.element.style.display = t ? "" : "none", this;
  }
  /**
   * 设置透明度
   */
  setOpacity(t) {
    return this.element.style.opacity = String(Math.max(0, Math.min(1, t))), this;
  }
  /**
   * 销毁实体
   */
  destroy() {
    this.unstack(), this.children.forEach((t) => t.destroy()), this.children.clear(), this.scene && this.scene.remove(this), this.eventDispatcher.destroy(), this.element.remove();
  }
}
class xe extends it {
  constructor(t = {}) {
    super(t), t.children && t.children.forEach(({ entity: e, offset: s }) => {
      (e instanceof it ? e : new it(e)).stackOn(this, s);
    });
  }
  /**
   * 添加子实体
   */
  addChild(t, e = { x: 0, y: 0, z: 0 }) {
    return t.stackOn(this, e), this;
  }
  /**
   * 移除子实体
   */
  removeChild(t) {
    return this.children.has(t) && t.unstack(), this;
  }
  /**
   * 获取所有后代实体（递归）
   */
  getAllDescendants() {
    const t = [], e = (s) => {
      s.getChildren().forEach((i) => {
        t.push(i), e(i);
      });
    };
    return e(this), t;
  }
  /**
   * 设置整体位置（移动所有子实体）
   */
  setPosition(t) {
    return super.setPosition(t), this;
  }
  /**
   * 对所有子实体应用特效
   */
  applyEffectToAll(t) {
    return this.applyEffect(t), this.children.forEach((e) => {
      e.applyEffect(t);
    }), this;
  }
  /**
   * 清除所有子实体的特效
   */
  clearAllEffects() {
    return this.clearEffects(), this.children.forEach((t) => {
      t.clearEffects();
    }), this;
  }
}
class _e {
  constructor(t, e, s = {}) {
    this.arrowElement = null, this.scene = null, this.id = K("connector"), this.fromEntity = t, this.toEntity = e, this.options = {
      color: s.color ?? "#666",
      width: s.width ?? 2,
      style: s.style ?? "solid",
      arrow: s.arrow ?? !1,
      curvature: s.curvature ?? 0
    }, this.element = this.createElement(), this.pathElement = this.createPath(), this.element.appendChild(this.pathElement), this.options.arrow && (this.arrowElement = this.createArrow(), this.element.appendChild(this.arrowElement)), this.eventDispatcher = new $t(this), this.bindDOMEvents();
  }
  /**
   * 创建 SVG 容器
   */
  createElement() {
    const t = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    return t.setAttribute("class", "isometric-connector"), Object.assign(t.style, {
      position: "absolute",
      top: "0",
      left: "0",
      width: "100%",
      height: "100%",
      pointerEvents: "none",
      overflow: "visible",
      zIndex: "100000"
    }), t;
  }
  /**
   * 创建路径元素
   */
  createPath() {
    const t = document.createElementNS("http://www.w3.org/2000/svg", "path");
    return t.setAttribute("fill", "none"), t.setAttribute("stroke", this.options.color), t.setAttribute("stroke-width", String(this.options.width)), t.style.pointerEvents = "stroke", t.style.cursor = "pointer", this.options.style === "dashed" ? t.setAttribute("stroke-dasharray", "8,4") : this.options.style === "dotted" && t.setAttribute("stroke-dasharray", "2,4"), t;
  }
  /**
   * 创建箭头
   */
  createArrow() {
    const t = document.createElementNS("http://www.w3.org/2000/svg", "polygon");
    return t.setAttribute("fill", this.options.color), t;
  }
  /**
   * 绑定 DOM 事件
   */
  bindDOMEvents() {
    this.pathElement.addEventListener("click", (t) => this.emitEvent("click", t)), this.pathElement.addEventListener("mouseenter", (t) => this.emitEvent("hover", t)), this.pathElement.addEventListener("mouseleave", (t) => this.emitEvent("hoverEnd", t));
  }
  /**
   * 触发事件
   */
  emitEvent(t, e) {
    const s = {
      x: e.clientX ?? 0,
      y: e.clientY ?? 0
    };
    this.eventDispatcher.emit(
      t,
      e,
      this.fromEntity.getAbsolutePosition(),
      s
    );
  }
  /**
   * 添加事件监听器
   */
  on(t, e) {
    return this.eventDispatcher.on(t, e), this;
  }
  /**
   * 移除事件监听器
   */
  off(t, e) {
    return this.eventDispatcher.off(t, e), this;
  }
  /**
   * 更新连线路径
   */
  update() {
    if (!this.scene) return;
    const t = this.fromEntity.getAbsolutePosition(), e = this.toEntity.getAbsolutePosition(), s = this.scene.getTransform(), i = s.isoToScreen(this.getCenterPosition(t, this.fromEntity)), n = s.isoToScreen(this.getCenterPosition(e, this.toEntity)), o = this.calculatePath(i, n);
    this.pathElement.setAttribute("d", o), this.arrowElement && this.updateArrow(i, n);
  }
  /**
   * 获取实体中心位置
   */
  getCenterPosition(t, e) {
    const s = e.getSize();
    return {
      x: t.x,
      y: t.y,
      z: t.z + s.depth / 2
    };
  }
  /**
   * 计算 SVG 路径
   */
  calculatePath(t, e) {
    const s = this.options.curvature ?? 0;
    if (s === 0)
      return `M ${t.x} ${t.y} L ${e.x} ${e.y}`;
    const i = (t.x + e.x) / 2, n = (t.y + e.y) / 2, o = e.x - t.x, h = e.y - t.y, a = Math.sqrt(o * o + h * h), c = -h / a * s * a * 0.5, l = o / a * s * a * 0.5, p = i + c, m = n + l;
    return `M ${t.x} ${t.y} Q ${p} ${m} ${e.x} ${e.y}`;
  }
  /**
   * 更新箭头位置
   */
  updateArrow(t, e) {
    if (!this.arrowElement) return;
    const s = e.x - t.x, i = e.y - t.y, n = Math.atan2(i, s), o = 10, h = e.x - o * Math.cos(n - Math.PI / 6), a = e.y - o * Math.sin(n - Math.PI / 6), c = e.x - o * Math.cos(n + Math.PI / 6), l = e.y - o * Math.sin(n + Math.PI / 6);
    this.arrowElement.setAttribute("points", `${e.x},${e.y} ${h},${a} ${c},${l}`);
  }
  /**
   * 附加到场景
   */
  attachToScene(t) {
    this.scene = t, t.getWrapper().appendChild(this.element), this.update();
  }
  /**
   * 从场景分离
   */
  detachFromScene() {
    this.element.parentNode?.removeChild(this.element), this.scene = null;
  }
  /**
   * 获取 DOM 元素
   */
  getElement() {
    return this.element;
  }
  /**
   * 设置颜色
   */
  setColor(t) {
    return this.options.color = t, this.pathElement.setAttribute("stroke", t), this.arrowElement?.setAttribute("fill", t), this;
  }
  /**
   * 设置线宽
   */
  setWidth(t) {
    return this.options.width = t, this.pathElement.setAttribute("stroke-width", String(t)), this;
  }
  /**
   * 销毁连线
   */
  destroy() {
    this.detachFromScene(), this.eventDispatcher.destroy();
  }
}
class be {
  constructor(t, e) {
    this.scene = null, this.visible = !1, this.boundHandlers = {}, this.id = K("tooltip"), this.target = t, this.options = {
      content: e.content,
      position: e.position ?? "top",
      offset: e.offset ?? { x: 0, y: 0 },
      trigger: e.trigger ?? "hover",
      className: e.className
    }, this.element = this.createElement(), this.bindTriggerEvents();
  }
  /**
   * 创建浮层 DOM 元素
   */
  createElement() {
    const t = document.createElement("div");
    t.className = "isometric-tooltip", this.options.className && t.classList.add(this.options.className), Object.assign(t.style, {
      position: "absolute",
      zIndex: "10000",
      padding: "8px 12px",
      backgroundColor: "rgba(0, 0, 0, 0.85)",
      color: "#fff",
      borderRadius: "4px",
      fontSize: "14px",
      lineHeight: "1.4",
      whiteSpace: "nowrap",
      pointerEvents: "none",
      opacity: "0",
      transform: "scale(0.9)",
      transition: "opacity 0.2s, transform 0.2s",
      boxShadow: "0 2px 8px rgba(0, 0, 0, 0.3)"
    });
    const e = this.options.content;
    return typeof e == "string" ? t.innerHTML = e : t.appendChild(e), t;
  }
  /**
   * 绑定触发事件
   */
  bindTriggerEvents() {
    const t = this.target.getElement();
    this.options.trigger === "hover" ? (this.boundHandlers.show = () => this.show(), this.boundHandlers.hide = () => this.hide(), t.addEventListener("mouseenter", this.boundHandlers.show), t.addEventListener("mouseleave", this.boundHandlers.hide)) : this.options.trigger === "click" && (this.boundHandlers.click = () => this.toggle(), t.addEventListener("click", this.boundHandlers.click));
  }
  /**
   * 设置内容
   */
  setContent(t) {
    return this.options.content = t, typeof t == "string" ? this.element.innerHTML = t : (this.element.innerHTML = "", this.element.appendChild(t)), this;
  }
  /**
   * 更新位置
   */
  updatePosition() {
    if (!this.scene) return;
    const t = this.target.getAbsolutePosition(), e = this.target.getSize(), s = {
      x: t.x,
      y: t.y,
      z: t.z + e.depth
    }, n = this.scene.getTransform().isoToScreen(s), o = this.element.getBoundingClientRect();
    let h = n.x - o.width / 2, a = n.y;
    const c = 10;
    switch (this.options.position) {
      case "top":
        a -= o.height + c;
        break;
      case "bottom":
        a += c;
        break;
      case "left":
        h -= o.width / 2 + c, a -= o.height / 2;
        break;
      case "right":
        h += o.width / 2 + c, a -= o.height / 2;
        break;
    }
    h += this.options.offset?.x ?? 0, a += this.options.offset?.y ?? 0, this.element.style.left = `${h}px`, this.element.style.top = `${a}px`;
  }
  /**
   * 显示浮层
   */
  show() {
    return this.scene ? (this.element.parentNode || this.scene.getContainer().appendChild(this.element), this.visible = !0, this.updatePosition(), requestAnimationFrame(() => {
      this.element.style.opacity = "1", this.element.style.transform = "scale(1)";
    }), this) : this;
  }
  /**
   * 隐藏浮层
   */
  hide() {
    return this.visible = !1, this.element.style.opacity = "0", this.element.style.transform = "scale(0.9)", this;
  }
  /**
   * 切换显示状态
   */
  toggle() {
    return this.visible ? this.hide() : this.show();
  }
  /**
   * 附加到场景
   */
  attachToScene(t) {
    this.scene = t;
  }
  /**
   * 从场景分离
   */
  detachFromScene() {
    this.hide(), this.element.parentNode?.removeChild(this.element), this.scene = null;
  }
  /**
   * 获取 DOM 元素
   */
  getElement() {
    return this.element;
  }
  /**
   * 是否可见
   */
  isVisible() {
    return this.visible;
  }
  /**
   * 销毁浮层
   */
  destroy() {
    const t = this.target.getElement();
    this.boundHandlers.show && t.removeEventListener("mouseenter", this.boundHandlers.show), this.boundHandlers.hide && t.removeEventListener("mouseleave", this.boundHandlers.hide), this.boundHandlers.click && t.removeEventListener("click", this.boundHandlers.click), this.detachFromScene();
  }
}
class $e {
  constructor() {
    this.effects = /* @__PURE__ */ new Map(), this.styleElement = null, this.initialized = !1, this.registerPresetEffects();
  }
  /**
   * 初始化样式表
   */
  initStyleSheet() {
    this.initialized || (this.styleElement = document.createElement("style"), this.styleElement.id = "isometric-effects-styles", document.head.appendChild(this.styleElement), this.styleElement.textContent = this.generateCSS(), this.initialized = !0);
  }
  /**
   * 注册预置特效
   */
  registerPresetEffects() {
    this.register("bounce", {
      name: "bounce",
      keyframes: [
        { transform: "translateY(0)" },
        { transform: "translateY(calc(-10px * var(--effect-intensity, 1)))" },
        { transform: "translateY(0)" }
      ],
      options: {
        duration: 500,
        iterations: 1 / 0,
        easing: "ease-in-out"
      }
    }), this.register("blink", {
      name: "blink",
      keyframes: [
        { opacity: "1" },
        { opacity: "0.3" },
        { opacity: "1" }
      ],
      options: {
        duration: 800,
        iterations: 1 / 0,
        easing: "ease-in-out"
      }
    }), this.register("glow", {
      name: "glow",
      keyframes: [
        { filter: "drop-shadow(0 0 5px var(--effect-color, #00ffff))" },
        { filter: "drop-shadow(0 0 20px var(--effect-color, #00ffff))" },
        { filter: "drop-shadow(0 0 5px var(--effect-color, #00ffff))" }
      ],
      options: {
        duration: 1500,
        iterations: 1 / 0,
        easing: "ease-in-out"
      }
    }), this.register("shake", {
      name: "shake",
      keyframes: [
        { transform: "translateX(0)" },
        { transform: "translateX(-5px)" },
        { transform: "translateX(5px)" },
        { transform: "translateX(-5px)" },
        { transform: "translateX(5px)" },
        { transform: "translateX(0)" }
      ],
      options: {
        duration: 500,
        iterations: 1 / 0,
        easing: "ease-in-out"
      }
    }), this.register("pulse", {
      name: "pulse",
      keyframes: [
        { transform: "scale(1)" },
        { transform: "scale(1.05)" },
        { transform: "scale(1)" }
      ],
      options: {
        duration: 1e3,
        iterations: 1 / 0,
        easing: "ease-in-out"
      }
    });
  }
  /**
   * 生成 CSS 样式
   */
  generateCSS() {
    return `
      /* Isometric Engine Effects */
      :root {
        --effect-duration: 1000ms;
        --effect-intensity: 1;
        --effect-color: #00ffff;
      }

      @keyframes isometric-bounce {
        0%, 100% { transform: translateY(0); }
        50% { transform: translateY(calc(-10px * var(--effect-intensity, 1))); }
      }

      @keyframes isometric-blink {
        0%, 100% { opacity: 1; }
        50% { opacity: 0.3; }
      }

      @keyframes isometric-glow {
        0%, 100% { filter: drop-shadow(0 0 5px var(--effect-color, #00ffff)); }
        50% { filter: drop-shadow(0 0 20px var(--effect-color, #00ffff)); }
      }

      @keyframes isometric-shake {
        0%, 100% { transform: translateX(0); }
        20% { transform: translateX(-5px); }
        40% { transform: translateX(5px); }
        60% { transform: translateX(-5px); }
        80% { transform: translateX(5px); }
      }

      @keyframes isometric-pulse {
        0%, 100% { transform: scale(1); }
        50% { transform: scale(1.05); }
      }

      .isometric-effect-bounce {
        animation: isometric-bounce var(--effect-duration, 500ms) ease-in-out infinite;
      }

      .isometric-effect-blink {
        animation: isometric-blink var(--effect-duration, 800ms) ease-in-out infinite;
      }

      .isometric-effect-glow {
        animation: isometric-glow var(--effect-duration, 1500ms) ease-in-out infinite;
      }

      .isometric-effect-shake {
        animation: isometric-shake var(--effect-duration, 500ms) ease-in-out infinite;
      }

      .isometric-effect-pulse {
        animation: isometric-pulse var(--effect-duration, 1000ms) ease-in-out infinite;
      }
    `;
  }
  /**
   * 注册自定义特效
   */
  register(t, e) {
    return this.effects.set(t, e), this;
  }
  /**
   * 获取特效定义
   */
  get(t) {
    return this.effects.get(t);
  }
  /**
   * 检查特效是否存在
   */
  has(t) {
    return this.effects.has(t);
  }
  /**
   * 获取所有特效名称
   */
  getNames() {
    return Array.from(this.effects.keys());
  }
  /**
   * 确保样式已注入
   */
  ensureStyles() {
    this.initStyleSheet();
  }
  /**
   * 销毁管理器
   */
  destroy() {
    this.styleElement && this.styleElement.parentNode && this.styleElement.parentNode.removeChild(this.styleElement), this.effects.clear(), this.initialized = !1;
  }
}
const dt = new $e();
class we {
  constructor(t) {
    this.id = K("light"), this.position = t.position, this.color = t.color ?? "#ffffff", this.intensity = t.intensity ?? 1, this.type = t.type ?? "point";
  }
}
class Ee {
  constructor() {
    this.lights = /* @__PURE__ */ new Map(), this._ambientColor = "rgba(100, 100, 100, 0.3)", this.shadowsEnabled = !0, this.shadowBlur = 10, this.shadowOffset = { x: 5, y: 5 };
  }
  /**
   * 添加光源
   */
  addLight(t) {
    const e = new we(t);
    return this.lights.set(e.id, e), e;
  }
  /**
   * 移除光源
   */
  removeLight(t) {
    return this.lights.delete(t);
  }
  /**
   * 获取光源
   */
  getLight(t) {
    return this.lights.get(t);
  }
  /**
   * 获取所有光源
   */
  getAllLights() {
    return Array.from(this.lights.values());
  }
  /**
   * 设置环境光
   */
  setAmbientLight(t) {
    return this._ambientColor = t, this;
  }
  /**
   * 获取环境光颜色
   */
  getAmbientColor() {
    return this._ambientColor;
  }
  /**
   * 启用/禁用阴影
   */
  setShadowsEnabled(t) {
    return this.shadowsEnabled = t, this;
  }
  /**
   * 设置阴影参数
   */
  setShadowParams(t, e) {
    return this.shadowBlur = t, this.shadowOffset = e, this;
  }
  /**
   * 计算元素在指定位置受到的光照效果
   */
  calculateLighting(t) {
    const e = {};
    if (this.lights.size === 0)
      return e;
    let s = 0;
    if (this.lights.forEach((i) => {
      if (i.type === "ambient")
        s += i.intensity;
      else {
        const n = Bt(t, i.position), o = i.type === "directional" ? 1 : 1 / (1 + n * 0.01);
        s += i.intensity * o;
      }
    }), s > 0) {
      const i = Math.min(1.5, s);
      e.filter = `brightness(${i})`;
    }
    return this.shadowsEnabled && (e.boxShadow = `${this.shadowOffset.x}px ${this.shadowOffset.y}px ${this.shadowBlur}px rgba(0, 0, 0, 0.3)`), e;
  }
  /**
   * 生成阴影 CSS
   */
  generateShadowCSS(t) {
    if (!this.shadowsEnabled || this.lights.size === 0)
      return "";
    let e = null, s = 0;
    if (this.lights.forEach((l) => {
      l.type !== "ambient" && l.intensity > s && (e = l, s = l.intensity);
    }), !e)
      return `${this.shadowOffset.x}px ${this.shadowOffset.y}px ${this.shadowBlur}px rgba(0, 0, 0, 0.3)`;
    const i = e, n = t.x - i.position.x, o = t.y - i.position.y, h = Math.sqrt(n * n + o * o) || 1, a = n / h * this.shadowOffset.x, c = o / h * this.shadowOffset.y;
    return `${a}px ${c}px ${this.shadowBlur}px rgba(0, 0, 0, 0.3)`;
  }
  /**
   * 清除所有光源
   */
  clear() {
    this.lights.clear();
  }
  /**
   * 销毁光影系统
   */
  destroy() {
    this.clear();
  }
}
class Se {
  constructor() {
    this.scenes = /* @__PURE__ */ new Map(), this.connectors = /* @__PURE__ */ new Map(), this.tooltips = /* @__PURE__ */ new Map(), dt.ensureStyles(), this.lightingSystem = new Ee();
  }
  /**
   * 创建场景
   */
  createScene(t, e) {
    const s = new ge(t, e), i = `scene-${this.scenes.size + 1}`;
    return this.scenes.set(i, s), s;
  }
  /**
   * 创建实体
   */
  createEntity(t) {
    return new it(t);
  }
  /**
   * 创建复合实体
   */
  createCompositeEntity(t) {
    return new xe(t);
  }
  /**
   * 创建连线
   */
  createConnector(t, e, s) {
    const i = new _e(t, e, s);
    return this.connectors.set(i.id, i), i;
  }
  /**
   * 将连线添加到场景
   */
  addConnectorToScene(t, e) {
    t.attachToScene(e);
  }
  /**
   * 创建浮层
   */
  createTooltip(t, e) {
    const s = new be(t, e);
    return this.tooltips.set(s.id, s), s;
  }
  /**
   * 将浮层添加到场景
   */
  addTooltipToScene(t, e) {
    t.attachToScene(e);
  }
  /**
   * 添加光源
   */
  addLight(t) {
    return this.lightingSystem.addLight(t);
  }
  /**
   * 获取光影系统
   */
  getLightingSystem() {
    return this.lightingSystem;
  }
  /**
   * 注册自定义特效
   */
  registerEffect(t, e) {
    return dt.register(t, e), this;
  }
  /**
   * 获取所有可用特效
   */
  getAvailableEffects() {
    return dt.getNames();
  }
  /**
   * 销毁引擎
   */
  destroy() {
    this.scenes.forEach((t) => t.destroy()), this.scenes.clear(), this.connectors.forEach((t) => t.destroy()), this.connectors.clear(), this.tooltips.forEach((t) => t.destroy()), this.tooltips.clear(), this.lightingSystem.destroy();
  }
}
Se.VERSION = "0.1.0";
class ps {
  constructor(t) {
    this.scene = null, this.id = K(t);
  }
  /**
   * 初始化事件分发器
   * 子类构造函数中调用
   */
  initEventDispatcher() {
    this.eventDispatcher = new $t(this);
  }
  /**
   * 触发事件
   */
  emitEvent(t, e, s, i) {
    const n = i ?? {
      x: e.clientX ?? 0,
      y: e.clientY ?? 0
    };
    this.eventDispatcher.emit(t, e, s, n);
  }
  /**
   * 添加事件监听器
   */
  on(t, e) {
    return this.eventDispatcher.on(t, e), this;
  }
  /**
   * 移除事件监听器
   */
  off(t, e) {
    return this.eventDispatcher.off(t, e), this;
  }
  /**
   * 附加到场景
   */
  attachToScene(t) {
    this.scene = t, this.onAttach();
  }
  /**
   * 从场景分离
   */
  detachFromScene() {
    this.onDetach(), this.scene = null;
  }
  /**
   * 附加到场景时的钩子
   */
  onAttach() {
  }
  /**
   * 从场景分离时的钩子
   */
  onDetach() {
  }
  /**
   * 获取 DOM 元素
   */
  getElement() {
    return this.element;
  }
  /**
   * 获取所属场景
   */
  getScene() {
    return this.scene;
  }
  /**
   * 销毁组件
   */
  destroy() {
    this.detachFromScene(), this.eventDispatcher?.destroy(), this.element?.remove();
  }
}
class ds {
  constructor(t = 0) {
    this.zOffset = t;
  }
  /**
   * 设置 z-index 偏移
   */
  setZOffset(t) {
    this.zOffset = t;
  }
  /**
   * 解析路由顺序
   */
  parseRoute(t) {
    return t === "auto" ? ["x", "y", "z"] : t.split("-").filter((e) => ["x", "y", "z"].includes(e));
  }
  /**
   * 等距偏移转屏幕偏移
   * 使用 CSS 3D 变换矩阵
   */
  isoOffsetToScreen(t, e, s) {
    return {
      x: gt * t - gt * e,
      y: mt * pt * t + mt * pt * e - zt * s
    };
  }
  /**
   * 计算屏幕路径（使用屏幕坐标）
   */
  calculateScreenPath(t, e, s, i, n, o) {
    const h = this.parseRoute(n), a = [], c = { ...s }, l = { x: t.x, y: t.y }, { x: p, y: m } = o;
    for (const f of h) {
      const g = i[f];
      if (Math.abs(c[f] - g) < 0.1) continue;
      const y = { ...l }, u = { ...c };
      u[f] = g;
      const C = {
        dx: u.x - c.x,
        dy: u.y - c.y,
        dz: u.z - c.z
      }, T = this.isoOffsetToScreen(C.dx, C.dy, C.dz), z = {
        x: l.x + T.x,
        y: l.y + T.y
      }, N = (c.x + c.y + u.x + u.y) / 2 + (c.z + u.z) / 2;
      a.push({
        screenPoints: [
          { x: y.x + p, y: y.y + m },
          { x: z.x + p, y: z.y + m }
        ],
        avgZ: N
      }), c.x = u.x, c.y = u.y, c.z = u.z, l.x = z.x, l.y = z.y;
    }
    return a;
  }
  /**
   * 等距坐标转屏幕坐标（简化版，不带 origin）
   * @deprecated 使用 calculateScreenPath 代替
   */
  isoToScreenSimple(t, e, s) {
    return {
      x: (t - e) * le,
      y: (t + e) * pe * pt - s * zt
    };
  }
  /**
   * 计算等距路径（沿轴向走线）
   * @deprecated 使用 calculateScreenPath 代替
   */
  calculateIsometricPath(t, e, s, i) {
    const n = this.parseRoute(s), o = [], h = { ...t }, { x: a, y: c } = i;
    for (const l of n) {
      const p = e[l];
      if (Math.abs(h[l] - p) < 0.1) continue;
      const m = this.isoToScreenSimple(h.x, h.y, h.z), f = { ...h };
      f[l] = p;
      const g = this.isoToScreenSimple(f.x, f.y, f.z), y = (h.x + h.y + f.x + f.y) / 2 + (h.z + f.z) / 2;
      o.push({
        screenPoints: [
          { x: m.x + a, y: m.y + c },
          { x: g.x + a, y: g.y + c }
        ],
        avgZ: y
      }), h.x = f.x, h.y = f.y, h.z = f.z;
    }
    return o;
  }
  /**
   * 生成 SVG 路径
   */
  generatePath(t) {
    if (t.length === 0)
      return { paths: [], arrowTransform: "" };
    const e = [], s = [];
    for (const a of t)
      s.length === 0 && s.push(a.screenPoints[0]), s.push(a.screenPoints[1]);
    if (s.length < 2)
      return { paths: [], arrowTransform: "" };
    let i = `M ${s[0].x.toFixed(2)} ${s[0].y.toFixed(2)}`;
    for (let a = 1; a < s.length; a++) {
      const c = s[a];
      i += ` L ${c.x.toFixed(2)} ${c.y.toFixed(2)}`;
    }
    const n = t.reduce((a, c) => a + c.avgZ, 0) / t.length, o = Math.round(n * 100) + this.zOffset;
    e.push({ path: i, zIndex: o });
    let h = "";
    if (s.length >= 2) {
      const a = s[s.length - 1], c = s[s.length - 2], l = Math.atan2(a.y - c.y, a.x - c.x) * 180 / Math.PI;
      h = `translate(${a.x}, ${a.y}) rotate(${l})`;
    }
    return { paths: e, arrowTransform: h };
  }
}
const nt = globalThis, wt = nt.ShadowRoot && (nt.ShadyCSS === void 0 || nt.ShadyCSS.nativeShadow) && "adoptedStyleSheets" in Document.prototype && "replace" in CSSStyleSheet.prototype, Et = /* @__PURE__ */ Symbol(), Ot = /* @__PURE__ */ new WeakMap();
let Yt = class {
  constructor(t, e, s) {
    if (this._$cssResult$ = !0, s !== Et) throw Error("CSSResult is not constructable. Use `unsafeCSS` or `css` instead.");
    this.cssText = t, this.t = e;
  }
  get styleSheet() {
    let t = this.o;
    const e = this.t;
    if (wt && t === void 0) {
      const s = e !== void 0 && e.length === 1;
      s && (t = Ot.get(e)), t === void 0 && ((this.o = t = new CSSStyleSheet()).replaceSync(this.cssText), s && Ot.set(e, t));
    }
    return t;
  }
  toString() {
    return this.cssText;
  }
};
const Ce = (r) => new Yt(typeof r == "string" ? r : r + "", void 0, Et), H = (r, ...t) => {
  const e = r.length === 1 ? r[0] : t.reduce(((s, i, n) => s + ((o) => {
    if (o._$cssResult$ === !0) return o.cssText;
    if (typeof o == "number") return o;
    throw Error("Value passed to 'css' function must be a 'css' function result: " + o + ". Use 'unsafeCSS' to pass non-literal values, but take care to ensure page security.");
  })(i) + r[n + 1]), r[0]);
  return new Yt(e, r, Et);
}, Ae = (r, t) => {
  if (wt) r.adoptedStyleSheets = t.map(((e) => e instanceof CSSStyleSheet ? e : e.styleSheet));
  else for (const e of t) {
    const s = document.createElement("style"), i = nt.litNonce;
    i !== void 0 && s.setAttribute("nonce", i), s.textContent = e.cssText, r.appendChild(s);
  }
}, Mt = wt ? (r) => r : (r) => r instanceof CSSStyleSheet ? ((t) => {
  let e = "";
  for (const s of t.cssRules) e += s.cssText;
  return Ce(e);
})(r) : r;
const { is: Pe, defineProperty: ze, getOwnPropertyDescriptor: Te, getOwnPropertyNames: ke, getOwnPropertySymbols: Oe, getPrototypeOf: Me } = Object, ht = globalThis, It = ht.trustedTypes, Ie = It ? It.emptyScript : "", Ne = ht.reactiveElementPolyfillSupport, q = (r, t) => r, ot = { toAttribute(r, t) {
  switch (t) {
    case Boolean:
      r = r ? Ie : null;
      break;
    case Object:
    case Array:
      r = r == null ? r : JSON.stringify(r);
  }
  return r;
}, fromAttribute(r, t) {
  let e = r;
  switch (t) {
    case Boolean:
      e = r !== null;
      break;
    case Number:
      e = r === null ? null : Number(r);
      break;
    case Object:
    case Array:
      try {
        e = JSON.parse(r);
      } catch {
        e = null;
      }
  }
  return e;
} }, St = (r, t) => !Pe(r, t), Nt = { attribute: !0, type: String, converter: ot, reflect: !1, useDefault: !1, hasChanged: St };
Symbol.metadata ??= /* @__PURE__ */ Symbol("metadata"), ht.litPropertyMetadata ??= /* @__PURE__ */ new WeakMap();
let X = class extends HTMLElement {
  static addInitializer(t) {
    this._$Ei(), (this.l ??= []).push(t);
  }
  static get observedAttributes() {
    return this.finalize(), this._$Eh && [...this._$Eh.keys()];
  }
  static createProperty(t, e = Nt) {
    if (e.state && (e.attribute = !1), this._$Ei(), this.prototype.hasOwnProperty(t) && ((e = Object.create(e)).wrapped = !0), this.elementProperties.set(t, e), !e.noAccessor) {
      const s = /* @__PURE__ */ Symbol(), i = this.getPropertyDescriptor(t, s, e);
      i !== void 0 && ze(this.prototype, t, i);
    }
  }
  static getPropertyDescriptor(t, e, s) {
    const { get: i, set: n } = Te(this.prototype, t) ?? { get() {
      return this[e];
    }, set(o) {
      this[e] = o;
    } };
    return { get: i, set(o) {
      const h = i?.call(this);
      n?.call(this, o), this.requestUpdate(t, h, s);
    }, configurable: !0, enumerable: !0 };
  }
  static getPropertyOptions(t) {
    return this.elementProperties.get(t) ?? Nt;
  }
  static _$Ei() {
    if (this.hasOwnProperty(q("elementProperties"))) return;
    const t = Me(this);
    t.finalize(), t.l !== void 0 && (this.l = [...t.l]), this.elementProperties = new Map(t.elementProperties);
  }
  static finalize() {
    if (this.hasOwnProperty(q("finalized"))) return;
    if (this.finalized = !0, this._$Ei(), this.hasOwnProperty(q("properties"))) {
      const e = this.properties, s = [...ke(e), ...Oe(e)];
      for (const i of s) this.createProperty(i, e[i]);
    }
    const t = this[Symbol.metadata];
    if (t !== null) {
      const e = litPropertyMetadata.get(t);
      if (e !== void 0) for (const [s, i] of e) this.elementProperties.set(s, i);
    }
    this._$Eh = /* @__PURE__ */ new Map();
    for (const [e, s] of this.elementProperties) {
      const i = this._$Eu(e, s);
      i !== void 0 && this._$Eh.set(i, e);
    }
    this.elementStyles = this.finalizeStyles(this.styles);
  }
  static finalizeStyles(t) {
    const e = [];
    if (Array.isArray(t)) {
      const s = new Set(t.flat(1 / 0).reverse());
      for (const i of s) e.unshift(Mt(i));
    } else t !== void 0 && e.push(Mt(t));
    return e;
  }
  static _$Eu(t, e) {
    const s = e.attribute;
    return s === !1 ? void 0 : typeof s == "string" ? s : typeof t == "string" ? t.toLowerCase() : void 0;
  }
  constructor() {
    super(), this._$Ep = void 0, this.isUpdatePending = !1, this.hasUpdated = !1, this._$Em = null, this._$Ev();
  }
  _$Ev() {
    this._$ES = new Promise(((t) => this.enableUpdating = t)), this._$AL = /* @__PURE__ */ new Map(), this._$E_(), this.requestUpdate(), this.constructor.l?.forEach(((t) => t(this)));
  }
  addController(t) {
    (this._$EO ??= /* @__PURE__ */ new Set()).add(t), this.renderRoot !== void 0 && this.isConnected && t.hostConnected?.();
  }
  removeController(t) {
    this._$EO?.delete(t);
  }
  _$E_() {
    const t = /* @__PURE__ */ new Map(), e = this.constructor.elementProperties;
    for (const s of e.keys()) this.hasOwnProperty(s) && (t.set(s, this[s]), delete this[s]);
    t.size > 0 && (this._$Ep = t);
  }
  createRenderRoot() {
    const t = this.shadowRoot ?? this.attachShadow(this.constructor.shadowRootOptions);
    return Ae(t, this.constructor.elementStyles), t;
  }
  connectedCallback() {
    this.renderRoot ??= this.createRenderRoot(), this.enableUpdating(!0), this._$EO?.forEach(((t) => t.hostConnected?.()));
  }
  enableUpdating(t) {
  }
  disconnectedCallback() {
    this._$EO?.forEach(((t) => t.hostDisconnected?.()));
  }
  attributeChangedCallback(t, e, s) {
    this._$AK(t, s);
  }
  _$ET(t, e) {
    const s = this.constructor.elementProperties.get(t), i = this.constructor._$Eu(t, s);
    if (i !== void 0 && s.reflect === !0) {
      const n = (s.converter?.toAttribute !== void 0 ? s.converter : ot).toAttribute(e, s.type);
      this._$Em = t, n == null ? this.removeAttribute(i) : this.setAttribute(i, n), this._$Em = null;
    }
  }
  _$AK(t, e) {
    const s = this.constructor, i = s._$Eh.get(t);
    if (i !== void 0 && this._$Em !== i) {
      const n = s.getPropertyOptions(i), o = typeof n.converter == "function" ? { fromAttribute: n.converter } : n.converter?.fromAttribute !== void 0 ? n.converter : ot;
      this._$Em = i;
      const h = o.fromAttribute(e, n.type);
      this[i] = h ?? this._$Ej?.get(i) ?? h, this._$Em = null;
    }
  }
  requestUpdate(t, e, s) {
    if (t !== void 0) {
      const i = this.constructor, n = this[t];
      if (s ??= i.getPropertyOptions(t), !((s.hasChanged ?? St)(n, e) || s.useDefault && s.reflect && n === this._$Ej?.get(t) && !this.hasAttribute(i._$Eu(t, s)))) return;
      this.C(t, e, s);
    }
    this.isUpdatePending === !1 && (this._$ES = this._$EP());
  }
  C(t, e, { useDefault: s, reflect: i, wrapped: n }, o) {
    s && !(this._$Ej ??= /* @__PURE__ */ new Map()).has(t) && (this._$Ej.set(t, o ?? e ?? this[t]), n !== !0 || o !== void 0) || (this._$AL.has(t) || (this.hasUpdated || s || (e = void 0), this._$AL.set(t, e)), i === !0 && this._$Em !== t && (this._$Eq ??= /* @__PURE__ */ new Set()).add(t));
  }
  async _$EP() {
    this.isUpdatePending = !0;
    try {
      await this._$ES;
    } catch (e) {
      Promise.reject(e);
    }
    const t = this.scheduleUpdate();
    return t != null && await t, !this.isUpdatePending;
  }
  scheduleUpdate() {
    return this.performUpdate();
  }
  performUpdate() {
    if (!this.isUpdatePending) return;
    if (!this.hasUpdated) {
      if (this.renderRoot ??= this.createRenderRoot(), this._$Ep) {
        for (const [i, n] of this._$Ep) this[i] = n;
        this._$Ep = void 0;
      }
      const s = this.constructor.elementProperties;
      if (s.size > 0) for (const [i, n] of s) {
        const { wrapped: o } = n, h = this[i];
        o !== !0 || this._$AL.has(i) || h === void 0 || this.C(i, void 0, n, h);
      }
    }
    let t = !1;
    const e = this._$AL;
    try {
      t = this.shouldUpdate(e), t ? (this.willUpdate(e), this._$EO?.forEach(((s) => s.hostUpdate?.())), this.update(e)) : this._$EM();
    } catch (s) {
      throw t = !1, this._$EM(), s;
    }
    t && this._$AE(e);
  }
  willUpdate(t) {
  }
  _$AE(t) {
    this._$EO?.forEach(((e) => e.hostUpdated?.())), this.hasUpdated || (this.hasUpdated = !0, this.firstUpdated(t)), this.updated(t);
  }
  _$EM() {
    this._$AL = /* @__PURE__ */ new Map(), this.isUpdatePending = !1;
  }
  get updateComplete() {
    return this.getUpdateComplete();
  }
  getUpdateComplete() {
    return this._$ES;
  }
  shouldUpdate(t) {
    return !0;
  }
  update(t) {
    this._$Eq &&= this._$Eq.forEach(((e) => this._$ET(e, this[e]))), this._$EM();
  }
  updated(t) {
  }
  firstUpdated(t) {
  }
};
X.elementStyles = [], X.shadowRootOptions = { mode: "open" }, X[q("elementProperties")] = /* @__PURE__ */ new Map(), X[q("finalized")] = /* @__PURE__ */ new Map(), Ne?.({ ReactiveElement: X }), (ht.reactiveElementVersions ??= []).push("2.1.1");
const Ct = globalThis, at = Ct.trustedTypes, Lt = at ? at.createPolicy("lit-html", { createHTML: (r) => r }) : void 0, qt = "$lit$", k = `lit$${Math.random().toFixed(9).slice(2)}$`, Wt = "?" + k, Le = `<${Wt}>`, D = document, V = () => D.createComment(""), G = (r) => r === null || typeof r != "object" && typeof r != "function", At = Array.isArray, Fe = (r) => At(r) || typeof r?.[Symbol.iterator] == "function", ft = `[ 	
\f\r]`, Y = /<(?:(!--|\/[^a-zA-Z])|(\/?[a-zA-Z][^>\s]*)|(\/?$))/g, Ft = /-->/g, Rt = />/g, L = RegExp(`>|${ft}(?:([^\\s"'>=/]+)(${ft}*=${ft}*(?:[^ 	
\f\r"'\`<>=]|("|')|))|$)`, "g"), Dt = /'/g, Ht = /"/g, Vt = /^(?:script|style|textarea|title)$/i, Re = (r) => (t, ...e) => ({ _$litType$: r, strings: t, values: e }), S = Re(1), U = /* @__PURE__ */ Symbol.for("lit-noChange"), x = /* @__PURE__ */ Symbol.for("lit-nothing"), Xt = /* @__PURE__ */ new WeakMap(), R = D.createTreeWalker(D, 129);
function Gt(r, t) {
  if (!At(r) || !r.hasOwnProperty("raw")) throw Error("invalid template strings array");
  return Lt !== void 0 ? Lt.createHTML(t) : t;
}
const De = (r, t) => {
  const e = r.length - 1, s = [];
  let i, n = t === 2 ? "<svg>" : t === 3 ? "<math>" : "", o = Y;
  for (let h = 0; h < e; h++) {
    const a = r[h];
    let c, l, p = -1, m = 0;
    for (; m < a.length && (o.lastIndex = m, l = o.exec(a), l !== null); ) m = o.lastIndex, o === Y ? l[1] === "!--" ? o = Ft : l[1] !== void 0 ? o = Rt : l[2] !== void 0 ? (Vt.test(l[2]) && (i = RegExp("</" + l[2], "g")), o = L) : l[3] !== void 0 && (o = L) : o === L ? l[0] === ">" ? (o = i ?? Y, p = -1) : l[1] === void 0 ? p = -2 : (p = o.lastIndex - l[2].length, c = l[1], o = l[3] === void 0 ? L : l[3] === '"' ? Ht : Dt) : o === Ht || o === Dt ? o = L : o === Ft || o === Rt ? o = Y : (o = L, i = void 0);
    const f = o === L && r[h + 1].startsWith("/>") ? " " : "";
    n += o === Y ? a + Le : p >= 0 ? (s.push(c), a.slice(0, p) + qt + a.slice(p) + k + f) : a + k + (p === -2 ? h : f);
  }
  return [Gt(r, n + (r[e] || "<?>") + (t === 2 ? "</svg>" : t === 3 ? "</math>" : "")), s];
};
class J {
  constructor({ strings: t, _$litType$: e }, s) {
    let i;
    this.parts = [];
    let n = 0, o = 0;
    const h = t.length - 1, a = this.parts, [c, l] = De(t, e);
    if (this.el = J.createElement(c, s), R.currentNode = this.el.content, e === 2 || e === 3) {
      const p = this.el.content.firstChild;
      p.replaceWith(...p.childNodes);
    }
    for (; (i = R.nextNode()) !== null && a.length < h; ) {
      if (i.nodeType === 1) {
        if (i.hasAttributes()) for (const p of i.getAttributeNames()) if (p.endsWith(qt)) {
          const m = l[o++], f = i.getAttribute(p).split(k), g = /([.?@])?(.*)/.exec(m);
          a.push({ type: 1, index: n, name: g[2], strings: f, ctor: g[1] === "." ? Xe : g[1] === "?" ? Ze : g[1] === "@" ? Ue : ct }), i.removeAttribute(p);
        } else p.startsWith(k) && (a.push({ type: 6, index: n }), i.removeAttribute(p));
        if (Vt.test(i.tagName)) {
          const p = i.textContent.split(k), m = p.length - 1;
          if (m > 0) {
            i.textContent = at ? at.emptyScript : "";
            for (let f = 0; f < m; f++) i.append(p[f], V()), R.nextNode(), a.push({ type: 2, index: ++n });
            i.append(p[m], V());
          }
        }
      } else if (i.nodeType === 8) if (i.data === Wt) a.push({ type: 2, index: n });
      else {
        let p = -1;
        for (; (p = i.data.indexOf(k, p + 1)) !== -1; ) a.push({ type: 7, index: n }), p += k.length - 1;
      }
      n++;
    }
  }
  static createElement(t, e) {
    const s = D.createElement("template");
    return s.innerHTML = t, s;
  }
}
function j(r, t, e = r, s) {
  if (t === U) return t;
  let i = s !== void 0 ? e._$Co?.[s] : e._$Cl;
  const n = G(t) ? void 0 : t._$litDirective$;
  return i?.constructor !== n && (i?._$AO?.(!1), n === void 0 ? i = void 0 : (i = new n(r), i._$AT(r, e, s)), s !== void 0 ? (e._$Co ??= [])[s] = i : e._$Cl = i), i !== void 0 && (t = j(r, i._$AS(r, t.values), i, s)), t;
}
class He {
  constructor(t, e) {
    this._$AV = [], this._$AN = void 0, this._$AD = t, this._$AM = e;
  }
  get parentNode() {
    return this._$AM.parentNode;
  }
  get _$AU() {
    return this._$AM._$AU;
  }
  u(t) {
    const { el: { content: e }, parts: s } = this._$AD, i = (t?.creationScope ?? D).importNode(e, !0);
    R.currentNode = i;
    let n = R.nextNode(), o = 0, h = 0, a = s[0];
    for (; a !== void 0; ) {
      if (o === a.index) {
        let c;
        a.type === 2 ? c = new Q(n, n.nextSibling, this, t) : a.type === 1 ? c = new a.ctor(n, a.name, a.strings, this, t) : a.type === 6 && (c = new je(n, this, t)), this._$AV.push(c), a = s[++h];
      }
      o !== a?.index && (n = R.nextNode(), o++);
    }
    return R.currentNode = D, i;
  }
  p(t) {
    let e = 0;
    for (const s of this._$AV) s !== void 0 && (s.strings !== void 0 ? (s._$AI(t, s, e), e += s.strings.length - 2) : s._$AI(t[e])), e++;
  }
}
class Q {
  get _$AU() {
    return this._$AM?._$AU ?? this._$Cv;
  }
  constructor(t, e, s, i) {
    this.type = 2, this._$AH = x, this._$AN = void 0, this._$AA = t, this._$AB = e, this._$AM = s, this.options = i, this._$Cv = i?.isConnected ?? !0;
  }
  get parentNode() {
    let t = this._$AA.parentNode;
    const e = this._$AM;
    return e !== void 0 && t?.nodeType === 11 && (t = e.parentNode), t;
  }
  get startNode() {
    return this._$AA;
  }
  get endNode() {
    return this._$AB;
  }
  _$AI(t, e = this) {
    t = j(this, t, e), G(t) ? t === x || t == null || t === "" ? (this._$AH !== x && this._$AR(), this._$AH = x) : t !== this._$AH && t !== U && this._(t) : t._$litType$ !== void 0 ? this.$(t) : t.nodeType !== void 0 ? this.T(t) : Fe(t) ? this.k(t) : this._(t);
  }
  O(t) {
    return this._$AA.parentNode.insertBefore(t, this._$AB);
  }
  T(t) {
    this._$AH !== t && (this._$AR(), this._$AH = this.O(t));
  }
  _(t) {
    this._$AH !== x && G(this._$AH) ? this._$AA.nextSibling.data = t : this.T(D.createTextNode(t)), this._$AH = t;
  }
  $(t) {
    const { values: e, _$litType$: s } = t, i = typeof s == "number" ? this._$AC(t) : (s.el === void 0 && (s.el = J.createElement(Gt(s.h, s.h[0]), this.options)), s);
    if (this._$AH?._$AD === i) this._$AH.p(e);
    else {
      const n = new He(i, this), o = n.u(this.options);
      n.p(e), this.T(o), this._$AH = n;
    }
  }
  _$AC(t) {
    let e = Xt.get(t.strings);
    return e === void 0 && Xt.set(t.strings, e = new J(t)), e;
  }
  k(t) {
    At(this._$AH) || (this._$AH = [], this._$AR());
    const e = this._$AH;
    let s, i = 0;
    for (const n of t) i === e.length ? e.push(s = new Q(this.O(V()), this.O(V()), this, this.options)) : s = e[i], s._$AI(n), i++;
    i < e.length && (this._$AR(s && s._$AB.nextSibling, i), e.length = i);
  }
  _$AR(t = this._$AA.nextSibling, e) {
    for (this._$AP?.(!1, !0, e); t !== this._$AB; ) {
      const s = t.nextSibling;
      t.remove(), t = s;
    }
  }
  setConnected(t) {
    this._$AM === void 0 && (this._$Cv = t, this._$AP?.(t));
  }
}
class ct {
  get tagName() {
    return this.element.tagName;
  }
  get _$AU() {
    return this._$AM._$AU;
  }
  constructor(t, e, s, i, n) {
    this.type = 1, this._$AH = x, this._$AN = void 0, this.element = t, this.name = e, this._$AM = i, this.options = n, s.length > 2 || s[0] !== "" || s[1] !== "" ? (this._$AH = Array(s.length - 1).fill(new String()), this.strings = s) : this._$AH = x;
  }
  _$AI(t, e = this, s, i) {
    const n = this.strings;
    let o = !1;
    if (n === void 0) t = j(this, t, e, 0), o = !G(t) || t !== this._$AH && t !== U, o && (this._$AH = t);
    else {
      const h = t;
      let a, c;
      for (t = n[0], a = 0; a < n.length - 1; a++) c = j(this, h[s + a], e, a), c === U && (c = this._$AH[a]), o ||= !G(c) || c !== this._$AH[a], c === x ? t = x : t !== x && (t += (c ?? "") + n[a + 1]), this._$AH[a] = c;
    }
    o && !i && this.j(t);
  }
  j(t) {
    t === x ? this.element.removeAttribute(this.name) : this.element.setAttribute(this.name, t ?? "");
  }
}
class Xe extends ct {
  constructor() {
    super(...arguments), this.type = 3;
  }
  j(t) {
    this.element[this.name] = t === x ? void 0 : t;
  }
}
class Ze extends ct {
  constructor() {
    super(...arguments), this.type = 4;
  }
  j(t) {
    this.element.toggleAttribute(this.name, !!t && t !== x);
  }
}
class Ue extends ct {
  constructor(t, e, s, i, n) {
    super(t, e, s, i, n), this.type = 5;
  }
  _$AI(t, e = this) {
    if ((t = j(this, t, e, 0) ?? x) === U) return;
    const s = this._$AH, i = t === x && s !== x || t.capture !== s.capture || t.once !== s.once || t.passive !== s.passive, n = t !== x && (s === x || i);
    i && this.element.removeEventListener(this.name, this, s), n && this.element.addEventListener(this.name, this, t), this._$AH = t;
  }
  handleEvent(t) {
    typeof this._$AH == "function" ? this._$AH.call(this.options?.host ?? this.element, t) : this._$AH.handleEvent(t);
  }
}
class je {
  constructor(t, e, s) {
    this.element = t, this.type = 6, this._$AN = void 0, this._$AM = e, this.options = s;
  }
  get _$AU() {
    return this._$AM._$AU;
  }
  _$AI(t) {
    j(this, t);
  }
}
const Be = Ct.litHtmlPolyfillSupport;
Be?.(J, Q), (Ct.litHtmlVersions ??= []).push("3.3.1");
const Ye = (r, t, e) => {
  const s = e?.renderBefore ?? t;
  let i = s._$litPart$;
  if (i === void 0) {
    const n = e?.renderBefore ?? null;
    s._$litPart$ = i = new Q(t.insertBefore(V(), n), n, void 0, e ?? {});
  }
  return i._$AI(r), i;
};
const Pt = globalThis;
class O extends X {
  constructor() {
    super(...arguments), this.renderOptions = { host: this }, this._$Do = void 0;
  }
  createRenderRoot() {
    const t = super.createRenderRoot();
    return this.renderOptions.renderBefore ??= t.firstChild, t;
  }
  update(t) {
    const e = this.render();
    this.hasUpdated || (this.renderOptions.isConnected = this.isConnected), super.update(t), this._$Do = Ye(e, this.renderRoot, this.renderOptions);
  }
  connectedCallback() {
    super.connectedCallback(), this._$Do?.setConnected(!0);
  }
  disconnectedCallback() {
    super.disconnectedCallback(), this._$Do?.setConnected(!1);
  }
  render() {
    return U;
  }
}
O._$litElement$ = !0, O.finalized = !0, Pt.litElementHydrateSupport?.({ LitElement: O });
const qe = Pt.litElementPolyfillSupport;
qe?.({ LitElement: O });
(Pt.litElementVersions ??= []).push("4.2.1");
const Jt = (r) => (t, e) => {
  e !== void 0 ? e.addInitializer((() => {
    customElements.define(r, t);
  })) : customElements.define(r, t);
};
const We = { attribute: !0, type: String, converter: ot, reflect: !1, hasChanged: St }, Ve = (r = We, t, e) => {
  const { kind: s, metadata: i } = e;
  let n = globalThis.litPropertyMetadata.get(i);
  if (n === void 0 && globalThis.litPropertyMetadata.set(i, n = /* @__PURE__ */ new Map()), s === "setter" && ((r = Object.create(r)).wrapped = !0), n.set(e.name, r), s === "accessor") {
    const { name: o } = e;
    return { set(h) {
      const a = t.get.call(this);
      t.set.call(this, h), this.requestUpdate(o, a, r);
    }, init(h) {
      return h !== void 0 && this.C(o, void 0, r, h), h;
    } };
  }
  if (s === "setter") {
    const { name: o } = e;
    return function(h) {
      const a = this[o];
      t.call(this, h), this.requestUpdate(o, a, r);
    };
  }
  throw Error("Unsupported decorator location: " + s);
};
function d(r) {
  return (t, e) => typeof e == "object" ? Ve(r, t, e) : ((s, i, n) => {
    const o = i.hasOwnProperty(n);
    return i.constructor.createProperty(n, s), o ? Object.getOwnPropertyDescriptor(i, n) : void 0;
  })(r, t, e);
}
function M(r) {
  return d({ ...r, state: !0, attribute: !1 });
}
var Ge = Object.defineProperty, b = (r, t, e, s) => {
  for (var i = void 0, n = r.length - 1, o; n >= 0; n--)
    (o = r[n]) && (i = o(t, e, i) || i);
  return i && Ge(t, e, i), i;
};
function v(r, t = 0) {
  const e = Number(r);
  return isNaN(e) ? t : e;
}
const tt = {
  tl: { u: 0, v: 0 },
  tc: { u: 0.5, v: 0 },
  tr: { u: 1, v: 0 },
  ml: { u: 0, v: 0.5 },
  mc: { u: 0.5, v: 0.5 },
  mr: { u: 1, v: 0.5 },
  bl: { u: 0, v: 1 },
  bc: { u: 0.5, v: 1 },
  br: { u: 1, v: 1 }
};
class _ extends O {
  constructor() {
    super(...arguments), this.x = 0, this.y = 0, this.z = 0, this.row = null, this.col = null, this.gridSize = 30, this.width = 100, this.height = 100, this.depth = 50, this.topColor = F.top, this.frontColor = F.left, this.rightColor = F.right, this.entityId = "", this.noPointer = !1, this._scene = null;
  }
  connectedCallback() {
    super.connectedCallback(), this._scene = this.closest("iso-scene"), this.style.setProperty("--entity-width", `${v(this.width, 100)}px`), this.style.setProperty("--entity-height", `${v(this.height, 100)}px`), this.style.setProperty("--entity-depth", `${v(this.depth, 50)}px`), this.style.setProperty("--entity-top-color", this.topColor), this.style.setProperty("--entity-front-color", this.frontColor), this.style.setProperty("--entity-right-color", this.rightColor), this.updatePosition();
  }
  updated(t) {
    t.has("width") && this.style.setProperty("--entity-width", `${v(this.width, 100)}px`), t.has("height") && this.style.setProperty("--entity-height", `${v(this.height, 100)}px`), t.has("depth") && this.style.setProperty("--entity-depth", `${v(this.depth, 50)}px`), (t.has("x") || t.has("y") || t.has("z") || t.has("width") || t.has("height") || t.has("row") || t.has("col") || t.has("gridSize")) && this.updatePosition();
  }
  /**
   * 更新位置
   */
  updatePosition() {
    const t = v(this.x, 0) - this.width / 2, e = v(this.y, 0) - this.height / 2, s = v(this.z, 0), i = this.row === null || Number.isNaN(Number(this.row)) ? null : v(this.row, 0), n = this.col === null || Number.isNaN(Number(this.col)) ? null : v(this.col, 0), o = v(this.gridSize, 30);
    let h = t, a = e;
    i !== null && n !== null && (h = i * o - n * o, a = i * o + n * o), this.style.transform = `translate3d(${h}px, ${a}px, ${s}px) `;
  }
  /**
   * 获取实体中心点的屏幕坐标
   */
  getConnectionPoint() {
    const t = this.getBoundingClientRect(), e = this._scene?.getBoundingClientRect() ?? { left: 0, top: 0 };
    return {
      x: t.left + t.width / 2 - e.left,
      y: t.top + t.height / 2 - e.top
    };
  }
  /**
   * 获取指定面和位置的等距坐标
   */
  getFaceConnectionPoint(t = "top", e = "mc") {
    const { width: s, height: i, depth: n } = this, { u: o, v: h } = tt[e] || tt.mc, a = v(s, 100), c = v(i, 100), l = v(n, 50), p = v(this.x, 0), m = v(this.y, 0), f = v(this.z, 0);
    let g = 0, y = 0, u = 0;
    switch (t) {
      case "top":
        g = a * (o - 0.5), y = c * (h - 0.5), u = l;
        break;
      case "bottom":
        g = a * (o - 0.5), y = c * (h - 0.5), u = 0;
        break;
      case "front":
        g = a * (o - 0.5), y = c / 2, u = l * (1 - h);
        break;
      case "back":
        g = a * (o - 0.5), y = -c / 2, u = l * (1 - h);
        break;
      case "left":
        g = -a / 2, y = c * (o - 0.5), u = l * (1 - h);
        break;
      case "right":
        g = a / 2, y = c * (o - 0.5), u = l * (1 - h);
        break;
    }
    return {
      x: p + g,
      y: m + y,
      z: f + u
    };
  }
  /**
   * 获取指定面和位置的屏幕坐标
   */
  getFaceConnectionPointScreen(t = "top", e = "mc") {
    const { width: s, height: i, depth: n } = this, { u: o, v: h } = tt[e] || tt.mc, a = v(s, 100), c = v(i, 100), l = v(n, 50), p = v(this.x, 0), m = v(this.y, 0), f = v(this.z, 0);
    let g = 0, y = 0, u = 0;
    switch (t) {
      case "top":
        g = a * (o - 0.5), y = c * (h - 0.5), u = l;
        break;
      case "bottom":
        g = a * (o - 0.5), y = c * (h - 0.5), u = 0;
        break;
      case "front":
        g = a * (o - 0.5), y = c / 2, u = l * (1 - h);
        break;
      case "back":
        g = a * (o - 0.5), y = -c / 2, u = l * (1 - h);
        break;
      case "left":
        g = -a / 2, y = c * (o - 0.5), u = l * (1 - h);
        break;
      case "right":
        g = a / 2, y = c * (o - 0.5), u = l * (1 - h);
        break;
    }
    const C = jt({ x: p, y: m, z: f }), { cosZ: T, sinZ: z, cosX: N, sinX: Qt } = xt(), te = T * g - T * y, ee = z * N * g + z * N * y - Qt * u, lt = {
      x: p + g,
      y: m + y,
      z: f + u
    };
    return {
      x: C.x + te,
      y: C.y + ee,
      z: lt.x + lt.y + lt.z
    };
  }
}
_.baseStyles = H`
    :host {
      display: block;
      position: absolute;
      cursor: pointer;
      user-select: none;
      transform-style: preserve-3d;
    }

    :host([no-pointer]) {
      pointer-events: none !important;
      cursor: default;
    }

    :host([no-pointer]) .face {
      pointer-events: none !important;
    }

    .face {
      position: absolute;
      backface-visibility: hidden;
      box-sizing: border-box;
      pointer-events: auto;
      overflow: hidden;
    }

    .face ::slotted(*) {
      width: 100%;
      height: 100%;
      display: flex;
      align-items: center;
      justify-content: center;
    }

    .shadow-overlay {
      position: absolute;
      top: 0;
      left: 0;
      width: 100%;
      height: 100%;
      background: rgba(0, 0, 0, 0.15);
      pointer-events: none;
    }
  `;
b([
  d({ type: Number })
], _.prototype, "x");
b([
  d({ type: Number })
], _.prototype, "y");
b([
  d({ type: Number })
], _.prototype, "z");
b([
  d({ type: Number })
], _.prototype, "row");
b([
  d({ type: Number })
], _.prototype, "col");
b([
  d({ type: Number, attribute: "grid-size" })
], _.prototype, "gridSize");
b([
  d({ type: Number })
], _.prototype, "width");
b([
  d({ type: Number })
], _.prototype, "height");
b([
  d({ type: Number })
], _.prototype, "depth");
b([
  d({ type: String, attribute: "top-color" })
], _.prototype, "topColor");
b([
  d({ type: String, attribute: "front-color" })
], _.prototype, "frontColor");
b([
  d({ type: String, attribute: "right-color" })
], _.prototype, "rightColor");
b([
  d({ type: String, attribute: "entity-id" })
], _.prototype, "entityId");
b([
  d({ type: Boolean, attribute: "no-pointer" })
], _.prototype, "noPointer");
b([
  M()
], _.prototype, "_scene");
class B extends _ {
  render() {
    return S`
        <!-- 顶面 -->
        <div class="face face-top">
          <slot name="top"></slot>
        </div>

        <!-- 前面 -->
        <div class="face face-front">
          <slot name="front"></slot>
        </div>

        <!-- 右面 -->
        <div class="face face-right">
          <slot name="right"></slot>
          <div class="shadow-overlay"></div>
        </div>
    `;
  }
}
B.styles = [
  _.baseStyles,
  H`
      /* 顶面：水平放置在 Z=depth 高度 */
      .face-top {
        width: var(--entity-width);
        height: var(--entity-height);
        background: var(--entity-top-color, #4CAF50);
        transform: translateZ(var(--entity-depth));
      }

      /* 前面：立在顶面前边缘 */
      .face-front {
        width: var(--entity-width);
        height: var(--entity-depth);
        background: var(--entity-front-color, #388E3C);
        transform: 
          rotateX(-90deg)
          translateY(calc(0px - var(--entity-depth) / 2))
          translateZ(calc(var(--entity-height) - var(--entity-depth) / 2));
      }

      /* 右面：立在顶面右边缘 */
      .face-right {
        width: var(--entity-height);
        height: var(--entity-depth);
        background: var(--entity-right-color, #2E7D32);
        transform: 
          rotateY(90deg)
          rotateZ(-90deg)
          translateZ(calc(var(--entity-width) - var(--entity-height) / 2))
          translateY(calc(0px - var(--entity-depth) / 2))
          translateX(calc(var(--entity-depth) / 2 - var(--entity-height) / 2));
      }
    `
];
customElements.get("iso-cube") || customElements.define("iso-cube", B);
var Je = Object.defineProperty, I = (r, t, e, s) => {
  for (var i = void 0, n = r.length - 1, o; n >= 0; n--)
    (o = r[n]) && (i = o(t, e, i) || i);
  return i && Je(t, e, i), i;
};
class A extends O {
  constructor() {
    super(...arguments), this.x = 0, this.y = 0, this.z = 0, this.width = 100, this.height = 100, this.color = "#8BC34A", this.entityId = "", this.noPointer = !1;
  }
  connectedCallback() {
    super.connectedCallback(), this._updateStyles(), this._updatePosition();
  }
  updated(t) {
    (t.has("width") || t.has("height") || t.has("color")) && this._updateStyles(), (t.has("x") || t.has("y") || t.has("z") || t.has("width") || t.has("height")) && this._updatePosition();
  }
  _updateStyles() {
    this.style.setProperty("--plane-width", `${this.width}px`), this.style.setProperty("--plane-height", `${this.height}px`), this.style.setProperty("--plane-color", this.color);
  }
  _updatePosition() {
    const t = this.x - this.width / 2, e = this.y - this.height / 2;
    this.style.transform = `translate3d(${t}px, ${e}px, ${this.z}px)`;
  }
  render() {
    return S`
      <div class="plane">
        <slot></slot>
      </div>
    `;
  }
}
A.styles = H`
    :host {
      display: block;
      position: absolute;
      transform-style: preserve-3d;
    }

    :host([no-pointer]) {
      pointer-events: none !important;
    }

    .plane {
      position: absolute;
      width: var(--plane-width, 100px);
      height: var(--plane-height, 100px);
      background: var(--plane-color, #8BC34A);
      transform-style: preserve-3d;
      overflow: hidden;
    }

    .plane ::slotted(*) {
      width: 100%;
      height: 100%;
    }
  `;
I([
  d({ type: Number })
], A.prototype, "x");
I([
  d({ type: Number })
], A.prototype, "y");
I([
  d({ type: Number })
], A.prototype, "z");
I([
  d({ type: Number })
], A.prototype, "width");
I([
  d({ type: Number })
], A.prototype, "height");
I([
  d({ type: String })
], A.prototype, "color");
I([
  d({ type: String, attribute: "entity-id" })
], A.prototype, "entityId");
I([
  d({ type: Boolean, attribute: "no-pointer" })
], A.prototype, "noPointer");
customElements.get("iso-plane") || customElements.define("iso-plane", A);
var Ke = Object.getOwnPropertyDescriptor, Kt = (r, t, e, s) => {
  for (var i = s > 1 ? void 0 : s ? Ke(t, e) : t, n = r.length - 1, o; n >= 0; n--)
    (o = r[n]) && (i = o(i) || i);
  return i;
};
let yt = class extends B {
};
yt.styles = [
  ...B.styles,
  H`
      .face-top {
        transform: 
          translateZ(var(--entity-depth))
          rotateX(-30deg)
      }
      .face-front {
        transform: 
          rotateX(-90deg)
          translateY(calc(0px - var(--entity-depth) / 2))
          translateZ(calc(var(--entity-height) / 2 - var(--entity-depth) / 2));
      }
      .face-right {
        display: none;
      }
    `
];
yt = Kt([
  Jt("iso-console-front")
], yt);
let vt = class extends B {
};
vt.styles = [
  ...B.styles,
  H`
      .face-top {
        width: var(--entity-height);
        height: var(--entity-width);
        transform: 
          translateX(calc(var(--entity-width) / 2 - var(--entity-height) / 2))
          translateY(calc(var(--entity-height) / 2 - var(--entity-width) / 2))
          translateZ(var(--entity-depth))
          rotateZ(-90deg)
          rotateX(-30deg)
      }
      .face-right {
        transform: 
          rotateY(90deg)
          rotateZ(-90deg)
          translateZ(calc(var(--entity-width) /2 - var(--entity-height) / 2))
          translateY(calc(0px - var(--entity-depth) / 2))
          translateX(calc(var(--entity-depth) / 2 - var(--entity-height) / 2));
      }
      .face-front {
        display: none;
      }
    `
];
vt = Kt([
  Jt("iso-console-right")
], vt);
var Qe = Object.defineProperty, w = (r, t, e, s) => {
  for (var i = void 0, n = r.length - 1, o; n >= 0; n--)
    (o = r[n]) && (i = o(t, e, i) || i);
  return i && Qe(t, e, i), i;
};
const ut = {
  top: { x: 0, y: 0, z: 1 },
  // 顶面朝上
  bottom: { x: 0, y: 0, z: -1 },
  // 底面朝下
  front: { x: 0, y: 1, z: 0 },
  // 前面朝向观察者 (+y)
  back: { x: 0, y: -1, z: 0 },
  // 后面背向观察者 (-y)
  left: { x: -1, y: 0, z: 0 },
  // 左面 (-x)
  right: { x: 1, y: 0, z: 0 }
  // 右面 (+x)
};
class $ extends O {
  constructor() {
    super(...arguments), this.from = "", this.to = "", this.route = "auto", this.color = "#00d4ff", this.width = 2, this.lineStyle = "solid", this.animation = "none", this.perpendicularLength = 0, this.selected = !1, this.particles = "", this._segments = [], this._particles = [], this._reverseParticles = [], this._fromEntity = null, this._toEntity = null, this._scene = null, this._resizeObserver = null, this._updateTimer = null, this._particleIdCounter = 0, this._lastEmitTime = 0, this._lastReverseEmitTime = 0, this._animationFrameId = null, this._totalPathLength = 0, this._lastFrameTime = 0, this._cachedFromConfig = null, this._cachedToConfig = null, this._cachedAnimationConfig = null, this._cachedParticleConfig = null, this._lastFrom = "", this._lastTo = "", this._lastAnimation = "", this._lastParticles = "", this._anglesHandler = (() => {
      this._updatePath();
    });
  }
  /**
   * 解析连接点字符串
   * 格式: "entityId" 或 "entityId@face:position"
   */
  _parseConnection(t) {
    const e = t.indexOf("@");
    if (e === -1)
      return {
        entityId: t,
        face: "top",
        position: "mc"
      };
    const s = t.substring(0, e), i = t.substring(e + 1), [n, o] = i.split(":");
    return {
      entityId: s,
      face: n || "top",
      position: o || "mc"
    };
  }
  /**
   * 解析动画字符串
   * 格式: "type" 或 "type speed" 或 "type speed color"
   */
  _parseAnimation(t) {
    const e = t.trim().split(/\s+/), s = e[0] || "none", i = e[1] ? parseFloat(e[1]) : 1, n = e[2] || "";
    return { type: s, speed: i, color: n };
  }
  /**
   * 解析粒子配置字符串
   * 格式: "color size rate speed effect direction trail"
   * 支持带单位的参数（顺序无关）：
   * - size: 数字或带 px 后缀，如 "8" 或 "8px"
   * - rate: 带 hz 后缀，如 "2hz"
   * - speed: 带 ms 后缀（毫秒转秒），如 "500ms"
   * - trail: 带 trail 后缀，如 "3trail"
   * 
   * 示例：
   * - "#fff 8px 2hz 500ms glow forward 3trail"
   * - "500ms 8px 2hz" (顺序无关)
   * - "8 2 0.5" (无单位按顺序解析)
   */
  _parseParticles(t) {
    const e = t.trim();
    if (!e || e === "none")
      return {
        enabled: !1,
        color: "",
        size: 8,
        rate: 2,
        speed: 0.5,
        effect: "glow",
        direction: "forward",
        trailLength: 3
      };
    const s = e.split(/\s+/), i = {
      enabled: !0,
      color: "",
      size: 8,
      rate: 2,
      speed: 0.5,
      effect: "glow",
      direction: "forward",
      trailLength: 3
    };
    let n = 0;
    for (const o of s) {
      const h = o.toLowerCase();
      if (o.startsWith("#") || h.startsWith("rgb") || h.startsWith("hsl"))
        i.color = o;
      else if (["none", "glow", "trail", "pulse", "rainbow", "spark"].includes(h))
        i.effect = h;
      else if (["forward", "backward", "bidirectional"].includes(h))
        i.direction = h;
      else if (h.endsWith("px")) {
        const a = parseFloat(h);
        isNaN(a) || (i.size = a);
      } else if (h.endsWith("hz")) {
        const a = parseFloat(h);
        isNaN(a) || (i.rate = a);
      } else if (h.endsWith("ms")) {
        const a = parseFloat(h);
        isNaN(a) || (i.speed = a / 1e3);
      } else if (h.endsWith("s") && !h.endsWith("ms")) {
        const a = parseFloat(h);
        isNaN(a) || (i.speed = a);
      } else if (h.endsWith("trail")) {
        const a = parseFloat(h);
        isNaN(a) || (i.trailLength = a);
      } else if (!isNaN(parseFloat(o))) {
        const a = parseFloat(o);
        switch (n) {
          case 0:
            i.size = a;
            break;
          case 1:
            i.rate = a;
            break;
          case 2:
            i.speed = a;
            break;
          case 3:
            i.trailLength = a;
            break;
        }
        n++;
      }
    }
    return i;
  }
  // 获取解析后的 from 配置
  get fromConfig() {
    return (this._lastFrom !== this.from || !this._cachedFromConfig) && (this._cachedFromConfig = this._parseConnection(this.from), this._lastFrom = this.from), this._cachedFromConfig;
  }
  // 获取解析后的 to 配置
  get toConfig() {
    return (this._lastTo !== this.to || !this._cachedToConfig) && (this._cachedToConfig = this._parseConnection(this.to), this._lastTo = this.to), this._cachedToConfig;
  }
  // 获取解析后的动画配置
  get animationConfig() {
    return (this._lastAnimation !== this.animation || !this._cachedAnimationConfig) && (this._cachedAnimationConfig = this._parseAnimation(this.animation), this._lastAnimation = this.animation), this._cachedAnimationConfig;
  }
  // 获取解析后的粒子配置
  get particleConfig() {
    return (this._lastParticles !== this.particles || !this._cachedParticleConfig) && (this._cachedParticleConfig = this._parseParticles(this.particles), this._lastParticles = this.particles), this._cachedParticleConfig;
  }
  // 兼容旧 API 的 getter
  get fromFace() {
    return this.fromConfig.face;
  }
  get fromPosition() {
    return this.fromConfig.position;
  }
  get toFace() {
    return this.toConfig.face;
  }
  get toPosition() {
    return this.toConfig.position;
  }
  connectedCallback() {
    super.connectedCallback(), this._scene = this.closest("iso-scene"), this._setupObservers(), this._updateCSSVariables(), setTimeout(() => {
      this._findEntities(), this._updatePath(), this._startParticleAnimation();
    }, 100), window.addEventListener("iso-angles-changed", this._anglesHandler);
  }
  disconnectedCallback() {
    super.disconnectedCallback(), this._cleanupObservers(), this._stopParticleAnimation(), this._updateTimer && clearTimeout(this._updateTimer), window.removeEventListener("iso-angles-changed", this._anglesHandler);
  }
  _setupObservers() {
    this._resizeObserver = new ResizeObserver(() => {
      this._scheduleUpdate();
    }), this._scene && this._resizeObserver.observe(this._scene);
  }
  _cleanupObservers() {
    this._resizeObserver?.disconnect();
  }
  _scheduleUpdate() {
    this._updateTimer && clearTimeout(this._updateTimer), this._updateTimer = window.setTimeout(() => {
      this._updatePath();
    }, 16);
  }
  _findEntities() {
    if (this._scene || (this._scene = this.closest("iso-scene")), !this._scene) return;
    const t = this.fromConfig.entityId, e = this.toConfig.entityId;
    t && (this._fromEntity = this._scene.querySelector(`[entity-id="${t}"]`)), e && (this._toEntity = this._scene.querySelector(`[entity-id="${e}"]`));
  }
  updated(t) {
    (t.has("from") || t.has("to")) && this._findEntities(), (t.has("color") || t.has("width") || t.has("animation") || t.has("particles")) && this._updateCSSVariables(), ["from", "to", "route", "perpendicularLength"].some((s) => t.has(s)) && this._updatePath(), t.has("particles") && (this.particleConfig.enabled ? this._startParticleAnimation() : (this._stopParticleAnimation(), this._particles = [], this._reverseParticles = []), this._particles = [], this._reverseParticles = [], this._lastEmitTime = 0, this._lastReverseEmitTime = 0);
  }
  /**
   * 更新 CSS 变量
   */
  _updateCSSVariables() {
    const t = this.animationConfig, e = this.particleConfig, s = v(this.width, 2), i = v(e.size, 8), n = t.color || this.color;
    this.style.setProperty("--connector-color", this.color), this.style.setProperty("--connector-width", `${s}px`), this.style.setProperty("--animate-speed", String(t.speed)), this.style.setProperty("--glow-color", n), this.style.setProperty("--particle-size", `${i}px`);
  }
  /**
   * 获取面的法线对应的主轴
   */
  _getFaceAxis(t) {
    const e = ut[t];
    return Math.abs(e.z) > 0.5 ? "z" : Math.abs(e.y) > 0.5 ? "y" : "x";
  }
  /**
   * 解析路由顺序（仅用于中间段）
   */
  _parseRoute(t) {
    return t === "auto" || t === "direct" ? ["x", "z", "y"] : t.split("-").filter((e) => ["x", "z", "y"].includes(e));
  }
  /**
   * 计算 3D 路径段
   * 
   * 路由策略：
   * 1. 如果 perpendicularLength > 0：从起点沿起点面的法线方向出发
   * 2. 中间按用户指定的轴顺序走线（自动补充缺失的轴）
   * 3. 如果 perpendicularLength > 0：沿终点面的法线方向进入终点
   */
  _updatePath() {
    if (!this._fromEntity || !this._toEntity || !this._scene) return;
    const t = this._fromEntity.getFaceConnectionPoint(this.fromFace, this.fromPosition), e = this._toEntity.getFaceConnectionPoint(this.toFace, this.toPosition), s = [];
    if (this.route === "direct") {
      const i = e.x - t.x, n = e.y - t.y, o = e.z - t.z, h = Math.sqrt(i * i + n * n + o * o);
      s.push({
        start: { ...t },
        end: { ...e },
        length: h,
        axis: "direct"
      });
    } else {
      const i = this._getFaceAxis(this.fromFace), n = this._getFaceAxis(this.toFace), o = ut[this.fromFace], h = ut[this.toFace], a = this.perpendicularLength, c = a > 0 ? {
        x: t.x + o.x * a,
        y: t.y + o.y * a,
        z: t.z + o.z * a
      } : { ...t }, l = a > 0 ? {
        x: e.x + h.x * a,
        y: e.y + h.y * a,
        z: e.z + h.z * a
      } : { ...e };
      a > 0 && s.push({
        start: { ...t },
        end: { ...c },
        length: a,
        axis: i
      });
      const p = this._parseRoute(this.route), f = ["x", "z", "y"].filter((u) => !p.includes(u)), g = [...p, ...f], y = { ...c };
      for (const u of g) {
        const C = l[u], T = C - y[u];
        if (Math.abs(T) < 0.1) continue;
        const z = { ...y }, N = { ...y };
        N[u] = C, s.push({
          start: z,
          end: N,
          length: Math.abs(T),
          axis: u
        }), y[u] = C;
      }
      a > 0 && s.push({
        start: { ...l },
        end: { ...e },
        length: a,
        axis: n
      });
    }
    this._segments = [...s], this._totalPathLength = s.reduce((i, n) => i + n.length, 0), this.requestUpdate();
  }
  // ========== 粒子动画相关方法 ==========
  /**
   * 启动粒子动画
   */
  _startParticleAnimation() {
    if (this._animationFrameId !== null || !this.particleConfig.enabled) return;
    this._lastFrameTime = 0;
    const t = (e) => {
      this._updateParticles(e), this._animationFrameId = requestAnimationFrame(t);
    };
    this._animationFrameId = requestAnimationFrame(t);
  }
  /**
   * 停止粒子动画
   */
  _stopParticleAnimation() {
    this._animationFrameId !== null && (cancelAnimationFrame(this._animationFrameId), this._animationFrameId = null);
  }
  /**
   * 更新粒子状态
   */
  _updateParticles(t) {
    const e = this.particleConfig;
    if (!e.enabled || this._totalPathLength === 0) return;
    this._lastFrameTime === 0 && (this._lastFrameTime = t);
    const s = (t - this._lastFrameTime) / 1e3;
    this._lastFrameTime = t;
    const i = 1e3 / e.rate;
    (e.direction === "forward" || e.direction === "bidirectional") && t - this._lastEmitTime >= i && (this._particles.push({
      id: this._particleIdCounter++,
      progress: 0,
      createdAt: t
    }), this._lastEmitTime = t), (e.direction === "backward" || e.direction === "bidirectional") && t - this._lastReverseEmitTime >= i && (this._reverseParticles.push({
      id: this._particleIdCounter++,
      progress: 1,
      createdAt: t
    }), this._lastReverseEmitTime = t), this._particles = this._particles.map((n) => ({
      ...n,
      progress: n.progress + e.speed * s
    })).filter((n) => n.progress <= 1), this._reverseParticles = this._reverseParticles.map((n) => ({
      ...n,
      progress: n.progress - e.speed * s
    })).filter((n) => n.progress >= 0), this.requestUpdate();
  }
  /**
   * 根据进度（0-1）计算粒子在 3D 空间中的位置
   */
  _getPositionAtProgress(t) {
    if (this._segments.length === 0)
      return { x: 0, y: 0, z: 0 };
    const e = t * this._totalPathLength;
    let s = 0;
    for (const n of this._segments) {
      if (s + n.length >= e) {
        const o = (e - s) / n.length;
        return {
          x: n.start.x + (n.end.x - n.start.x) * o,
          y: n.start.y + (n.end.y - n.start.y) * o,
          z: n.start.z + (n.end.z - n.start.z) * o
        };
      }
      s += n.length;
    }
    return { ...this._segments[this._segments.length - 1].end };
  }
  /**
   * 获取彩虹颜色（rainbow 特效）
   */
  _getRainbowColor(t) {
    return `hsl(${t * 360 % 360}, 100%, 60%)`;
  }
  /**
   * 渲染单个粒子
   */
  _renderParticle(t, e = !1) {
    const s = this.particleConfig, i = this._getPositionAtProgress(t.progress);
    let o = s.color || this.color;
    if (s.effect === "rainbow" && (o = this._getRainbowColor(t.progress)), s.effect === "trail")
      return this._renderTrailParticle(t, e);
    const h = s.effect !== "none" ? `effect-${s.effect}` : "";
    return S`
      <div 
        class="particle ${h}"
        style="
          --px: ${i.x}px;
          --py: ${i.y}px;
          --pz: ${i.z}px;
          --particle-color: ${o};
          --particle-glow-color: ${o};
        "
      ></div>
    `;
  }
  /**
   * 渲染带拖尾的粒子
   */
  _renderTrailParticle(t, e) {
    const s = this.particleConfig, i = s.color || this.color, n = v(s.size, 8), o = v(s.trailLength, 3), h = 0.03, a = [];
    for (let c = 0; c <= o; c++) {
      const l = e ? Math.min(1, t.progress + c * h) : Math.max(0, t.progress - c * h), p = this._getPositionAtProgress(l), m = n * (1 - c * 0.15), f = 1 - c * (0.8 / o), g = m / 2;
      let y = i;
      s.effect === "rainbow" && (y = this._getRainbowColor(l)), a.push(S`
        <div 
          class="trail-dot"
          style="
            width: ${m}px;
            height: ${m}px;
            background: ${y};
            opacity: ${f};
            margin-left: ${-g}px;
            margin-top: ${-g}px;
            transform: translate3d(${p.x}px, ${p.y}px, ${p.z}px);
            box-shadow: 0 0 ${4 * f}px ${y};
          "
        ></div>
      `);
    }
    return S`<div class="particle-trail">${a}</div>`;
  }
  /**
   * 计算线段的 3D 变换
   * 
   * 线段默认沿 X 轴正方向绘制（从原点向右），通过旋转来改变方向
   * 在等距空间中：
   * - X 轴：rotateZ(0°) 或 rotateZ(180°)
   * - Y 轴：rotateZ(90°) 或 rotateZ(-90°)
   * - Z 轴：rotateX(-90°) 或 rotateX(90°)（绕 X 轴旋转，让线段朝上或朝下）
   */
  _getSegmentTransform(t) {
    const { start: e, end: s, axis: i } = t;
    let n = `translate3d(${e.x}px, ${e.y}px, ${e.z}px)`;
    if (i === "direct") {
      const o = s.x - e.x, h = s.y - e.y, a = s.z - e.z, c = Math.atan2(h, o) * 180 / Math.PI, l = Math.sqrt(o * o + h * h), p = Math.atan2(a, l) * 180 / Math.PI;
      n += ` rotateZ(${c}deg) rotateX(${-p}deg)`;
    } else if (i === "x") {
      const o = s.x > e.x ? 0 : 180;
      n += ` rotateZ(${o}deg)`;
    } else if (i === "y") {
      const o = s.y > e.y ? 90 : -90;
      n += ` rotateZ(${o}deg)`;
    } else if (i === "z") {
      const o = s.z > e.z ? -90 : 90;
      n += ` rotateY(${o}deg)`;
    }
    return n;
  }
  _getLineStyleClass() {
    const t = this.animationConfig, e = [];
    return this.lineStyle === "dashed" ? e.push("line-dashed") : this.lineStyle === "dotted" ? e.push("line-dotted") : t.type === "flow" && e.push("line-flow"), e.join(" ");
  }
  _getAnimationClass() {
    switch (this.animationConfig.type) {
      case "flow":
        return "animate-flow";
      case "pulse":
        return "animate-pulse";
      case "glow":
        return "animate-glow glow";
      default:
        return "";
    }
  }
  render() {
    const t = this.particleConfig, e = this._getAnimationClass(), s = this._getLineStyleClass(), i = this.selected ? "selected" : "", n = this._fromEntity ? this._fromEntity.getFaceConnectionPoint(this.fromFace, this.fromPosition) : null, o = this._toEntity ? this._toEntity.getFaceConnectionPoint(this.toFace, this.toPosition) : null;
    return S`
      ${this._segments.map((h) => S`
        <div 
          class="line-segment"
          style="transform: ${this._getSegmentTransform(h)};"
        >
          <div 
            class="line-inner ${s} ${e} ${i}"
            style="--line-length: ${h.length}px;"
          ></div>
        </div>
      `)}
      
      <!-- 起点锚点 -->
      ${n ? S`
        <div 
          class="anchor-point from-anchor"
          style="transform: translate3d(${n.x}px, ${n.y}px, ${n.z}px);"
        ></div>
      ` : ""}
      
      <!-- 终点锚点 -->
      ${o ? S`
        <div 
          class="anchor-point to-anchor"
          style="transform: translate3d(${o.x}px, ${o.y}px, ${o.z}px);"
        ></div>
      ` : ""}

      <!-- 粒子（发光小球） -->
      ${t.enabled ? S`
        ${this._particles.map((h) => this._renderParticle(h, !1))}
        ${this._reverseParticles.map((h) => this._renderParticle(h, !0))}
      ` : ""}
    `;
  }
}
$.styles = H`
    :host {
      /* 核心样式变量 */
      --connector-color: #00d4ff;
      --connector-width: 2px;
      --animate-speed: 1;
      --glow-color: var(--connector-color);
      --particle-size: 8px;
      --particle-color: var(--connector-color);
      --particle-glow-color: var(--particle-color);

      display: block;
      position: absolute;
      left: 0;
      top: 0;
      pointer-events: none;
      transform-style: preserve-3d;
    }

    .line-segment {
      position: absolute;
      left: 0;
      top: 0;
      transform-style: preserve-3d;
      pointer-events: none;
    }

    .line-inner {
      position: absolute;
      transform-origin: left center;
      pointer-events: auto;
      cursor: pointer;
      width: var(--line-length);
      height: var(--connector-width);
      margin-top: calc(var(--connector-width) / -2);
      background: var(--connector-color);
    }

    .line-inner.selected {
      box-shadow: 0 0 6px 2px rgba(255, 255, 255, 0.8);
    }

    .line-inner.glow {
      box-shadow: 0 0 4px var(--glow-color),
                  0 0 8px var(--glow-color);
    }

    /* 虚线样式 */
    .line-inner.line-dashed {
      background: repeating-linear-gradient(
        90deg,
        var(--connector-color) 0px,
        var(--connector-color) 8px,
        transparent 8px,
        transparent 12px
      );
    }

    /* 点线样式 */
    .line-inner.line-dotted {
      background: repeating-linear-gradient(
        90deg,
        var(--connector-color) 0px,
        var(--connector-color) 2px,
        transparent 2px,
        transparent 6px
      );
    }

    /* 流动动画背景 */
    .line-inner.line-flow {
      background: repeating-linear-gradient(
        90deg,
        var(--connector-color) 0px,
        var(--connector-color) 12px,
        transparent 12px,
        transparent 24px
      );
      background-size: 24px 100%;
    }

    @keyframes flow {
      from { background-position: 0 0; }
      to { background-position: 24px 0; }
    }

    @keyframes pulse {
      0%, 100% { opacity: 1; }
      50% { opacity: 0.4; }
    }

    @keyframes glow-pulse {
      0%, 100% { 
        box-shadow: 0 0 2px var(--glow-color),
                    0 0 4px var(--glow-color);
      }
      50% { 
        box-shadow: 0 0 6px var(--glow-color),
                    0 0 12px var(--glow-color);
      }
    }

    .animate-flow {
      animation: flow calc(1s / var(--animate-speed)) linear infinite;
    }

    .animate-pulse {
      animation: pulse calc(2s / var(--animate-speed)) ease-in-out infinite;
    }

    .animate-glow {
      animation: glow-pulse calc(2s / var(--animate-speed)) ease-in-out infinite;
    }

    /* 锚点样式 */
    .anchor-point {
      position: absolute;
      left: 0;
      top: 0;
      width: 8px;
      height: 8px;
      border-radius: 50%;
      margin-left: -4px;
      margin-top: -4px;
      pointer-events: none;
      transform-style: preserve-3d;
    }

    .from-anchor {
      background: #00ff00;
      box-shadow: 0 0 4px #00ff00;
    }

    .to-anchor {
      background: #ff0000;
      box-shadow: 0 0 4px #ff0000;
    }

    /* ========== 粒子（发光小球）样式 ========== */
    .particle {
      position: absolute;
      left: 0;
      top: 0;
      width: var(--particle-size);
      height: var(--particle-size);
      margin-left: calc(var(--particle-size) / -2);
      margin-top: calc(var(--particle-size) / -2);
      background: var(--particle-color);
      border-radius: 50%;
      pointer-events: none;
      transform-style: preserve-3d;
      transform: translate3d(var(--px), var(--py), var(--pz));
      will-change: transform;
    }

    /* 发光特效 */
    .particle.effect-glow {
      box-shadow: 0 0 6px 2px var(--particle-glow-color),
                  0 0 12px 4px var(--particle-glow-color);
    }

    /* 脉冲特效 */
    .particle.effect-pulse {
      box-shadow: 0 0 6px 2px var(--particle-glow-color),
                  0 0 12px 4px var(--particle-glow-color);
      animation: particle-pulse 0.5s ease-in-out infinite;
    }

    @keyframes particle-pulse {
      0%, 100% { 
        transform: translate3d(var(--px), var(--py), var(--pz)) scale(1);
        opacity: 1;
      }
      50% { 
        transform: translate3d(var(--px), var(--py), var(--pz)) scale(1.3);
        opacity: 0.7;
      }
    }

    /* 火花特效 */
    .particle.effect-spark {
      box-shadow: 0 0 4px 1px var(--particle-glow-color),
                  0 0 8px 2px var(--particle-glow-color),
                  0 0 16px 4px var(--particle-glow-color);
      animation: particle-spark 0.3s ease-in-out infinite;
    }

    @keyframes particle-spark {
      0%, 100% { opacity: 1; }
      50% { opacity: 0.6; }
    }

    /* 彩虹特效 - 带发光 */
    .particle.effect-rainbow {
      box-shadow: 0 0 8px 3px var(--particle-glow-color),
                  0 0 16px 6px var(--particle-glow-color);
    }

    /* 拖尾容器 */
    .particle-trail {
      position: absolute;
      left: 0;
      top: 0;
      pointer-events: none;
      transform-style: preserve-3d;
    }

    .trail-dot {
      position: absolute;
      left: 0;
      top: 0;
      border-radius: 50%;
      pointer-events: none;
      transform-style: preserve-3d;
    }
  `;
w([
  d({ type: String })
], $.prototype, "from");
w([
  d({ type: String })
], $.prototype, "to");
w([
  d({ type: String })
], $.prototype, "route");
w([
  d({ type: String })
], $.prototype, "color");
w([
  d({ type: Number })
], $.prototype, "width");
w([
  d({ type: String, attribute: "line-style" })
], $.prototype, "lineStyle");
w([
  d({ type: String })
], $.prototype, "animation");
w([
  d({ type: Number, attribute: "perpendicular-length" })
], $.prototype, "perpendicularLength");
w([
  d({ type: Boolean, reflect: !0 })
], $.prototype, "selected");
w([
  d({ type: String })
], $.prototype, "particles");
w([
  M()
], $.prototype, "_segments");
w([
  M()
], $.prototype, "_particles");
w([
  M()
], $.prototype, "_reverseParticles");
customElements.get("iso-connector") || customElements.define("iso-connector", $);
var ts = Object.defineProperty, P = (r, t, e, s) => {
  for (var i = void 0, n = r.length - 1, o; n >= 0; n--)
    (o = r[n]) && (i = o(t, e, i) || i);
  return i && ts(t, e, i), i;
};
class E extends O {
  constructor() {
    super(...arguments), this.centerOrigin = !1, this.width = 800, this.height = 500, this.originX = 0, this.originY = 0, this.perspective = 0, this._originPosition = { x: 0, y: 0 }, this._rotateX = ae(), this._rotateZ = oe(), this._perspective = 0, this._resizeObserver = null, this._anglesHandler = ((t) => {
      const { rotateX: e, rotateZ: s, perspective: i } = t.detail;
      e !== void 0 && s !== void 0 && (he(e, s), this._rotateX = e, this._rotateZ = s), i !== void 0 && (this._perspective = i);
    });
  }
  connectedCallback() {
    super.connectedCallback(), this._perspective = this.perspective, this._resizeObserver = new ResizeObserver(() => {
      this._updateOrigin(), this._updateConnectors();
    }), this._resizeObserver.observe(this), window.addEventListener("iso-angles-changed", this._anglesHandler);
  }
  disconnectedCallback() {
    super.disconnectedCallback(), this._resizeObserver?.disconnect(), window.removeEventListener("iso-angles-changed", this._anglesHandler);
  }
  firstUpdated() {
    this._updateOrigin(), setTimeout(() => {
      this._updateConnectors();
    }, 100);
  }
  updated(t) {
    (t.has("centerOrigin") || t.has("originX") || t.has("originY")) && this._updateOrigin();
  }
  _updateOrigin() {
    let t = this.originX, e = this.originY;
    if (this.centerOrigin) {
      const s = this.getBoundingClientRect();
      t = s.width / 2, e = s.height / 2;
    }
    this._originPosition = { x: t, y: e };
  }
  _updateConnectors() {
    this.querySelectorAll("iso-connector").forEach((e) => {
      e.requestUpdate();
    });
  }
  /**
   * 获取原点位置
   */
  getOriginPosition() {
    return { ...this._originPosition };
  }
  /**
   * 获取所有实体
   */
  getEntities() {
    return Array.from(this.querySelectorAll("[entity-id]"));
  }
  /**
   * 根据 ID 获取实体
   */
  getEntityById(t) {
    return this.querySelector(`[entity-id="${t}"]`);
  }
  /**
   * 获取所有连线
   */
  getConnectors() {
    return Array.from(this.querySelectorAll("iso-connector"));
  }
  render() {
    const { x: t, y: e } = this._originPosition, s = Number(this.width) || 800, i = Number(this.height) || 500, n = Number(this._perspective) || 0, o = n > 0 ? `perspective: ${n}px; perspective-origin: ${t}px ${e}px;` : "";
    return S`
      <div class="scene-container" style="width: ${s}px; height: ${i}px; ${o}">
        <div class="origin" style="left: ${t}px; top: ${e}px; transform: rotateX(${this._rotateX}deg) rotateZ(${this._rotateZ}deg);">
          <div class="connectors-layer">
            <slot name="connectors"></slot>
          </div>
          <slot></slot>
        </div>
      </div>
    `;
  }
}
E.styles = H`
    :host {
      display: block;
      position: relative;
      overflow: visible;
      /* 让点击事件穿透到子元素 */
      pointer-events: none;
    }

    .scene-container {
      position: relative;
      width: 100%;
      height: 100%;
      /* 保持 3D 上下文 */
      transform-style: preserve-3d;
      overflow: visible;
      /* 容器本身不捕获事件，让子元素可以被点击 */
      pointer-events: none;
    }

    .origin {
      position: absolute;
      /* 关键：保持 3D 上下文传递给子元素 */
      transform-style: preserve-3d;
      /* 原点元素本身不应该捕获事件 */
      pointer-events: none;
    }

    .origin ::slotted(*) {
      pointer-events: auto;
    }

    .connectors-layer {
      position: absolute;
      top: 0;
      left: 0;
      pointer-events: none;
      /* 保持 3D 上下文 */
      transform-style: preserve-3d;
    }
  `;
P([
  d({ type: Boolean, attribute: "center-origin" })
], E.prototype, "centerOrigin");
P([
  d({ type: Number })
], E.prototype, "width");
P([
  d({ type: Number })
], E.prototype, "height");
P([
  d({ type: Number, attribute: "origin-x" })
], E.prototype, "originX");
P([
  d({ type: Number, attribute: "origin-y" })
], E.prototype, "originY");
P([
  d({ type: Number })
], E.prototype, "perspective");
P([
  M()
], E.prototype, "_originPosition");
P([
  M()
], E.prototype, "_rotateX");
P([
  M()
], E.prototype, "_rotateZ");
P([
  M()
], E.prototype, "_perspective");
customElements.get("iso-scene") || customElements.define("iso-scene", E);
export {
  ps as BaseComponent,
  le as COS_ANGLE,
  pt as COS_X,
  gt as COS_Z,
  xe as CompositeEntity,
  _e as Connector,
  ve as CubeRenderer,
  F as DEFAULT_COLORS,
  es as DEFAULT_COLORS_RGB,
  ss as DEFAULT_ISO_ANGLE,
  ne as DEFAULT_PERSPECTIVE,
  ie as DEFAULT_POSITION,
  re as DEFAULT_SCALE,
  se as DEFAULT_SIZE,
  $e as EffectManager,
  it as Entity,
  $t as EventDispatcher,
  W as ISO_ANGLES,
  is as ISO_ANGLE_DEG,
  ce as ISO_ANGLE_RAD,
  $ as IsoConnector,
  yt as IsoConsoleFront,
  vt as IsoConsoleRight,
  B as IsoCube,
  _ as IsoEntity,
  A as IsoPlane,
  E as IsoScene,
  Se as IsometricEngine,
  me as IsometricEventImpl,
  we as Light,
  Ee as LightingSystem,
  ds as PathCalculator,
  Zt as ROTATE_X_DEG,
  Ut as ROTATE_X_RAD,
  _t as ROTATE_Z_DEG,
  bt as ROTATE_Z_RAD,
  pe as SIN_ANGLE,
  zt as SIN_X,
  mt as SIN_Z,
  ge as Scene,
  be as Tooltip,
  ue as Transform,
  Tt as adjustBrightness,
  ye as brighten,
  fe as calculateZIndex,
  os as createHorizontalRow,
  hs as createStack,
  as as createVerticalColumn,
  kt as darken,
  Se as default,
  Bt as distance,
  dt as effectManager,
  K as generateId,
  ae as getRotateXDeg,
  oe as getRotateZDeg,
  xt as getTrigValues,
  ns as gridToIso,
  rs as isoToGrid,
  jt as isoToScreen,
  et as parseHexColor,
  ls as resetAllIdCounters,
  cs as resetIdCounter,
  st as rgbToHex,
  Z as runtimeAngles,
  de as screenToIso,
  he as updateAngles
};
