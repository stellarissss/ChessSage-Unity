import { CSSResult } from 'lit';
import { LitElement } from 'lit';
import { TemplateResult } from 'lit';

/**
 * 调整颜色亮度
 * @param color 十六进制颜色
 * @param amount 调整量，正值变亮，负值变暗
 */
export declare function adjustBrightness(color: string, amount: number): string;

/** 解析后的动画配置 */
declare interface AnimationConfig {
    type: AnimationType;
    speed: number;
    color: string;
}

declare type AnimationType = 'none' | 'flow' | 'pulse' | 'glow';

/**
 * 组件基类
 * 提供统一的 ID 生成、事件处理、场景管理等功能
 */
export declare abstract class BaseComponent<T extends BaseComponent<T> = any> {
    /** 唯一标识符 */
    readonly id: string;
    /** DOM 元素 */
    protected element: HTMLElement | SVGElement;
    /** 所属场景 */
    protected scene: Scene | null;
    /** 事件分发器 */
    protected eventDispatcher: EventDispatcher<T>;
    constructor(idPrefix: string);
    /**
     * 初始化事件分发器
     * 子类构造函数中调用
     */
    protected initEventDispatcher(): void;
    /**
     * 触发事件
     */
    protected emitEvent(type: EventType, originalEvent: Event, position: IsometricPosition, screenPosition?: ScreenPosition): void;
    /**
     * 添加事件监听器
     */
    on(type: EventType, handler: EventHandler<T>): this;
    /**
     * 移除事件监听器
     */
    off(type: EventType, handler: EventHandler<T>): this;
    /**
     * 附加到场景
     */
    attachToScene(scene: Scene): void;
    /**
     * 从场景分离
     */
    detachFromScene(): void;
    /**
     * 附加到场景时的钩子
     */
    protected onAttach(): void;
    /**
     * 从场景分离时的钩子
     */
    protected onDetach(): void;
    /**
     * 获取 DOM 元素
     */
    getElement(): HTMLElement | SVGElement;
    /**
     * 获取所属场景
     */
    getScene(): Scene | null;
    /**
     * 销毁组件
     */
    destroy(): void;
}

/**
 * 使颜色变亮
 */
export declare function brighten(rgb: RGB, amount: number): RGB;

/**
 * 计算 Z-Index 排序值
 * 用于正确的遮挡关系
 *
 * 在等距视图中（rotateX=60°, rotateZ=45°），使用底部面的右下角作为比较点：
 * 1. 右下角的 x+y 权重最高：x+y 越大越靠前（靠近观察者）
 * 2. z（底部高度）次之：同一 x+y 时，z 越高越靠前
 *
 * 注意：对于包含关系（如半透明外壳），需要通过 CSS z-index 覆盖
 *
 * @param pos - 实体中心的等距坐标
 * @param depth - 实体高度（未使用）
 * @param width - 实体宽度（用于计算右下角）
 * @param height - 实体高度（用于计算右下角）
 */
export declare function calculateZIndex(pos: IsometricPosition, _depth?: number, width?: number, height?: number): number;

/**
 * 复合实体
 * 支持将多个实体组合成一个整体
 */
export declare class CompositeEntity extends Entity {
    constructor(options?: CompositeEntityOptions);
    /**
     * 添加子实体
     */
    addChild(entity: Entity, offset?: IsometricPosition): this;
    /**
     * 移除子实体
     */
    removeChild(entity: Entity): this;
    /**
     * 获取所有后代实体（递归）
     */
    getAllDescendants(): Entity[];
    /**
     * 设置整体位置（移动所有子实体）
     */
    setPosition(pos: Partial<IsometricPosition>): this;
    /**
     * 对所有子实体应用特效
     */
    applyEffectToAll(effect: Parameters<Entity['applyEffect']>[0]): this;
    /**
     * 清除所有子实体的特效
     */
    clearAllEffects(): this;
}

/**
 * 复合实体选项
 */
export declare interface CompositeEntityOptions extends EntityOptions {
    /** 子实体配置 */
    children?: Array<{
        entity: Entity | EntityOptions;
        offset: IsometricPosition;
    }>;
}

/** 解析后的连接点配置 */
declare interface ConnectionConfig {
    entityId: string;
    face: FaceType_2;
    position: PositionType_2;
}

/**
 * 连线组件
 * 用于连接两个实体
 */
export declare class Connector {
    /** 唯一标识符 */
    readonly id: string;
    /** DOM 元素 (SVG) */
    protected element: SVGSVGElement;
    /** 路径元素 */
    protected pathElement: SVGPathElement;
    /** 箭头元素 */
    protected arrowElement: SVGPolygonElement | null;
    /** 起始实体 */
    protected fromEntity: Entity;
    /** 目标实体 */
    protected toEntity: Entity;
    /** 所属场景 */
    protected scene: Scene | null;
    /** 事件分发器 */
    protected eventDispatcher: EventDispatcher<Connector>;
    /** 配置选项 */
    protected options: ConnectorOptions;
    constructor(from: Entity, to: Entity, options?: ConnectorOptions);
    /**
     * 创建 SVG 容器
     */
    protected createElement(): SVGSVGElement;
    /**
     * 创建路径元素
     */
    protected createPath(): SVGPathElement;
    /**
     * 创建箭头
     */
    protected createArrow(): SVGPolygonElement;
    /**
     * 绑定 DOM 事件
     */
    protected bindDOMEvents(): void;
    /**
     * 触发事件
     */
    protected emitEvent(type: EventType, originalEvent: Event): void;
    /**
     * 添加事件监听器
     */
    on(type: EventType, handler: EventHandler<Connector>): this;
    /**
     * 移除事件监听器
     */
    off(type: EventType, handler: EventHandler<Connector>): this;
    /**
     * 更新连线路径
     */
    update(): void;
    /**
     * 获取实体中心位置
     */
    protected getCenterPosition(pos: IsometricPosition, entity: Entity): IsometricPosition;
    /**
     * 计算 SVG 路径
     */
    protected calculatePath(from: {
        x: number;
        y: number;
    }, to: {
        x: number;
        y: number;
    }): string;
    /**
     * 更新箭头位置
     */
    protected updateArrow(from: {
        x: number;
        y: number;
    }, to: {
        x: number;
        y: number;
    }): void;
    /**
     * 附加到场景
     */
    attachToScene(scene: Scene): void;
    /**
     * 从场景分离
     */
    detachFromScene(): void;
    /**
     * 获取 DOM 元素
     */
    getElement(): SVGSVGElement;
    /**
     * 设置颜色
     */
    setColor(color: string): this;
    /**
     * 设置线宽
     */
    setWidth(width: number): this;
    /**
     * 销毁连线
     */
    destroy(): void;
}

/**
 * 连线配置选项
 */
export declare interface ConnectorOptions {
    /** 线条颜色 */
    color?: string;
    /** 线条宽度 */
    width?: number;
    /** 线条样式 */
    style?: 'solid' | 'dashed' | 'dotted';
    /** 是否显示箭头 */
    arrow?: boolean;
    /** 曲线弯曲程度 */
    curvature?: number;
}

export declare const COS_ANGLE: number;

export declare const COS_X: number;

/** 预计算的 cos/sin 值 */
export declare const COS_Z: number;

/**
 * 创建一排水平对齐的位置（在等距视图中看起来是水平的）
 *
 * 在等距视图中，要让元素看起来在同一水平线上，
 * 需要让 x 增加的同时 y 减少相同的量
 *
 * @param count - 元素数量
 * @param spacing - 元素间距
 * @param start - 起始等距坐标
 */
export declare function createHorizontalRow(count: number, spacing: number, start?: IsometricPosition): IsometricPosition[];

/**
 * 创建堆叠的位置（在 Z 轴上堆叠）
 */
export declare function createStack(count: number, heightSpacing: number, start?: IsometricPosition): IsometricPosition[];

/**
 * 创建一列垂直对齐的位置（在等距视图中看起来是垂直的）
 *
 * 在等距视图中，要让元素看起来在同一垂直线上，
 * 需要让 x 和 y 同时增加相同的量
 *
 * @param count - 元素数量
 * @param spacing - 元素间距
 * @param start - 起始等距坐标
 */
export declare function createVerticalColumn(count: number, spacing: number, start?: IsometricPosition): IsometricPosition[];

/**
 * 立方体渲染器
 * 负责创建和更新等距立方体的 DOM 结构
 */
export declare class CubeRenderer {
    private container;
    private size;
    private colors;
    constructor(container: HTMLElement, size: Size3D, baseColor?: string);
    /**
     * 计算三个面的颜色
     */
    private calculateColors;
    /**
     * 创建立方体
     */
    render(): void;
    /**
     * 创建单个面
     */
    private createFace;
    /**
     * 更新尺寸
     */
    updateSize(size: Size3D): void;
    /**
     * 更新颜色
     */
    updateColors(baseColor: string): void;
    /**
     * 设置光照强度
     * @param intensity 光照强度 0-100
     */
    setLightIntensity(intensity: number): void;
    /**
     * 获取指定面的元素
     */
    getFaceElement(face: 'top' | 'left' | 'right'): HTMLElement | null;
    /**
     * 设置面的内容
     */
    setFaceContent(face: 'top' | 'left' | 'right', content: string | HTMLElement): void;
}

/**
 * 使颜色变暗
 */
export declare function darken(rgb: RGB, amount: number): RGB;

/**
 * 默认立方体颜色
 */
export declare const DEFAULT_COLORS: {
    readonly top: "#6C9BCF";
    readonly left: "#4A7AB0";
    readonly right: "#3A6691";
};

/**
 * 默认立方体颜色 RGB 值
 */
export declare const DEFAULT_COLORS_RGB: {
    readonly top: {
        readonly r: 108;
        readonly g: 155;
        readonly b: 207;
    };
    readonly left: {
        readonly r: 74;
        readonly g: 122;
        readonly b: 176;
    };
    readonly right: {
        readonly r: 58;
        readonly g: 102;
        readonly b: 145;
    };
};

/**
 * 默认等距角度（rotateX，保留以兼容旧代码）
 */
export declare const DEFAULT_ISO_ANGLE: 60;

/**
 * 默认透视距离
 */
export declare const DEFAULT_PERSPECTIVE = 1000;

/**
 * 默认位置
 */
export declare const DEFAULT_POSITION: IsometricPosition;

/**
 * 默认缩放比例
 */
export declare const DEFAULT_SCALE = 1;

/**
 * 默认尺寸
 */
export declare const DEFAULT_SIZE: Size3D;

/**
 * 计算两个等距坐标之间的距离
 */
export declare function distance(a: IsometricPosition, b: IsometricPosition): number;

/**
 * 特效定义
 */
export declare interface EffectDefinition {
    /** 特效名称 */
    name: string;
    /** CSS 动画关键帧 */
    keyframes: Keyframe[];
    /** 动画选项 */
    options: KeyframeAnimationOptions;
}

/**
 * 特效管理器
 * 管理和注册特效
 */
export declare class EffectManager {
    /** 已注册的特效 */
    private effects;
    /** 样式表元素 */
    private styleElement;
    /** 是否已初始化 */
    private initialized;
    constructor();
    /**
     * 初始化样式表
     */
    private initStyleSheet;
    /**
     * 注册预置特效
     */
    private registerPresetEffects;
    /**
     * 生成 CSS 样式
     */
    private generateCSS;
    /**
     * 注册自定义特效
     */
    register(name: string, definition: EffectDefinition): this;
    /**
     * 获取特效定义
     */
    get(name: string): EffectDefinition | undefined;
    /**
     * 检查特效是否存在
     */
    has(name: string): boolean;
    /**
     * 获取所有特效名称
     */
    getNames(): string[];
    /**
     * 确保样式已注入
     */
    ensureStyles(): void;
    /**
     * 销毁管理器
     */
    destroy(): void;
}

export declare const effectManager: EffectManager;

/**
 * 特效配置选项
 */
export declare interface EffectOptions {
    /** 特效类型 */
    type: EffectType | string;
    /** 持续时间 (ms) */
    duration?: number;
    /** 是否循环 */
    loop?: boolean;
    /** 动画延迟 (ms) */
    delay?: number;
    /** 特效强度 (0-1) */
    intensity?: number;
    /** 自定义颜色 */
    color?: string;
}

/**
 * 特效类型
 */
export declare type EffectType = 'bounce' | 'blink' | 'glow' | 'shake' | 'pulse';

/**
 * 实体组件
 * 场景中可渲染的基础组件
 */
export declare class Entity {
    /** 唯一标识符 */
    readonly id: string;
    /** DOM 元素 */
    protected element: HTMLElement;
    /** 等距坐标位置 */
    protected position: IsometricPosition;
    /** 三维尺寸 */
    protected size: Size3D;
    /** 所属场景 */
    protected scene: Scene | null;
    /** 事件分发器 */
    protected eventDispatcher: EventDispatcher<Entity>;
    /** 父实体（用于堆叠） */
    protected parent: Entity | null;
    /** 子实体集合 */
    protected children: Set<Entity>;
    /** 相对于父实体的偏移 */
    protected stackOffset: IsometricPosition;
    /** 当前应用的特效 */
    protected activeEffects: Set<string>;
    /** 配置选项 */
    protected options: EntityOptions;
    /** 立方体渲染器 */
    protected cubeRenderer: CubeRenderer;
    constructor(options?: EntityOptions);
    /**
     * 创建实体 DOM 元素
     */
    protected createElement(): HTMLElement;
    /**
     * 更新 CSS 变换
     */
    updateTransform(): void;
    /**
     * 绑定 DOM 事件
     */
    protected bindDOMEvents(): void;
    /**
     * 触发事件
     */
    protected emitEvent(type: EventType, originalEvent: Event): void;
    /**
     * 添加事件监听器
     */
    on(type: EventType, handler: EventHandler<Entity>): this;
    /**
     * 移除事件监听器
     */
    off(type: EventType, handler: EventHandler<Entity>): this;
    /**
     * 设置位置
     */
    setPosition(pos: Partial<IsometricPosition>): this;
    /**
     * 获取相对位置
     */
    getPosition(): IsometricPosition;
    /**
     * 获取绝对位置（考虑父实体）
     */
    getAbsolutePosition(): IsometricPosition;
    /**
     * 设置尺寸
     */
    setSize(size: Partial<Size3D>): this;
    /**
     * 获取尺寸
     */
    getSize(): Size3D;
    /**
     * 设置纹理
     */
    setTexture(texture: string | HTMLElement): this;
    /**
     * 设置指定面的 innerHTML
     */
    setFaceInnerHTML(face: 'top' | 'left' | 'right', content: string | HTMLElement): this;
    /**
     * 获取指定面的 DOM 元素
     */
    getFaceElement(face: 'top' | 'left' | 'right'): HTMLElement | null;
    /**
     * 设置光照强度
     */
    setLightIntensity(intensity: number): this;
    /**
     * 附加到场景
     */
    attachToScene(scene: Scene): void;
    /**
     * 从场景分离
     */
    detachFromScene(): void;
    /**
     * 获取 DOM 元素
     */
    getElement(): HTMLElement;
    /**
     * 堆叠到另一个实体上
     */
    stackOn(parent: Entity, offset?: IsometricPosition): this;
    /**
     * 取消堆叠
     */
    unstack(): this;
    /**
     * 获取父实体
     */
    getParent(): Entity | null;
    /**
     * 获取子实体
     */
    getChildren(): Entity[];
    /**
     * 应用特效
     */
    applyEffect(effect: EffectOptions): this;
    /**
     * 移除特效
     */
    removeEffect(type: string): this;
    /**
     * 移除所有特效
     */
    clearEffects(): this;
    /**
     * 设置可见性
     */
    setVisible(visible: boolean): this;
    /**
     * 设置透明度
     */
    setOpacity(opacity: number): this;
    /**
     * 销毁实体
     */
    destroy(): void;
}

/**
 * 实体配置选项
 */
export declare interface EntityOptions {
    /** 等距坐标位置 */
    position?: IsometricPosition;
    /** 三维尺寸 */
    size?: Size3D;
    /** 纹理内容：可以是 HTML 字符串、DOM 元素或图片 URL */
    texture?: string | HTMLElement;
    /** 是否可堆叠 */
    stackable?: boolean;
    /** CSS 类名 */
    className?: string;
    /** 自定义样式 */
    style?: Partial<CSSStyleDeclaration>;
}

/**
 * 事件分发器
 * 管理事件监听和分发
 */
export declare class EventDispatcher<T = unknown> {
    /** 事件监听器映射 */
    private listeners;
    /** 目标对象 */
    private target;
    constructor(target: T);
    /**
     * 添加事件监听器
     */
    on(type: EventType, handler: EventHandler<T>): this;
    /**
     * 移除事件监听器
     */
    off(type: EventType, handler: EventHandler<T>): this;
    /**
     * 添加一次性事件监听器
     */
    once(type: EventType, handler: EventHandler<T>): this;
    /**
     * 触发事件
     */
    emit(type: EventType, originalEvent: Event, position: IsometricPosition, screenPosition: ScreenPosition): void;
    /**
     * 检查是否有指定类型的监听器
     */
    hasListeners(type: EventType): boolean;
    /**
     * 移除所有监听器
     */
    removeAllListeners(type?: EventType): this;
    /**
     * 销毁事件分发器
     */
    destroy(): void;
}

/**
 * 事件处理器
 */
export declare type EventHandler<T = unknown> = (event: IsometricEvent<T>) => void;

/**
 * 事件类型
 */
export declare type EventType = 'click' | 'hover' | 'hoverEnd' | 'drag' | 'dragStart' | 'dragEnd';

export declare type FaceType = 'top' | 'bottom' | 'front' | 'back' | 'left' | 'right';

declare type FaceType_2 = 'top' | 'bottom' | 'front' | 'back' | 'left' | 'right';

/**
 * 生成唯一 ID
 * @param prefix ID 前缀
 */
export declare function generateId(prefix: string): string;

/** 获取当前 rotateX 角度（度） */
export declare function getRotateXDeg(): number;

/** 获取当前 rotateZ 角度（度） */
export declare function getRotateZDeg(): number;

/** 获取预计算的三角函数值 */
export declare function getTrigValues(): {
    cosZ: number;
    sinZ: number;
    cosX: number;
    sinX: number;
};

/**
 * 网格坐标系 - 用于更直观的布局
 *
 * 在等距视图中：
 * - row（行）：沿着屏幕左下到右上方向（等距 X 轴）
 * - col（列）：沿着屏幕右下到左上方向（等距 Y 轴）
 * - layer（层）：垂直高度（等距 Z 轴）
 *
 * 这样布局时：
 * - 同一行的元素在视觉上是水平对齐的
 * - 同一列的元素在视觉上是垂直对齐的
 */
export declare interface GridPosition {
    row: number;
    col: number;
    layer?: number;
}

/**
 * 网格坐标转等距坐标
 *
 * @param grid - 网格坐标 { row, col, layer }
 * @param cellSize - 单元格大小（默认 20）
 * @param origin - 网格原点的等距坐标（默认 {0, 0, 0}）
 */
export declare function gridToIso(grid: GridPosition, cellSize?: number, origin?: IsometricPosition): IsometricPosition;

export declare const ISO_ANGLE_DEG: 45;

export declare const ISO_ANGLE_RAD: number;

/**
 * 等距视图角度配置
 *
 * rotateX: 绕 X 轴旋转角度（俯视倾斜角），控制 Y 轴压缩程度
 *          60° 是标准等距视图，值越小越接近俯视图
 *
 * rotateZ: 绕 Z 轴旋转角度（平面旋转角），控制 X/Y 轴方向
 *          45° 使 X 轴向右下，Y 轴向左下
 */
export declare const ISO_ANGLES: {
    readonly rotateX: 60;
    readonly rotateZ: 45;
};

/**
 * 等距连线 Web Component
 *
 * 使用 3D 变换在等距空间中绘制连线，和实体一样由场景层统一做旋转变换
 *
 * 属性格式：
 * - from/to: "entityId" 或 "entityId@face:position"（如 "box1@bottom:mr"）
 * - animation: "type" 或 "type speed" 或 "type speed color"（如 "flow 1.5 #ff0000"）
 * - particles: "color size rate speed effect direction trail"（如 "#fff 8 2 0.5 glow forward 3"）
 */
export declare class IsoConnector extends LitElement {
    from: string;
    to: string;
    route: string;
    color: string;
    width: number;
    lineStyle: 'solid' | 'dashed' | 'dotted';
    animation: string;
    perpendicularLength: number;
    selected: boolean;
    particles: string;
    private _segments;
    private _particles;
    private _reverseParticles;
    private _fromEntity;
    private _toEntity;
    private _scene;
    private _resizeObserver;
    private _updateTimer;
    private _particleIdCounter;
    private _lastEmitTime;
    private _lastReverseEmitTime;
    private _animationFrameId;
    private _totalPathLength;
    private _lastFrameTime;
    private _cachedFromConfig;
    private _cachedToConfig;
    private _cachedAnimationConfig;
    private _cachedParticleConfig;
    private _lastFrom;
    private _lastTo;
    private _lastAnimation;
    private _lastParticles;
    /**
     * 解析连接点字符串
     * 格式: "entityId" 或 "entityId@face:position"
     */
    private _parseConnection;
    /**
     * 解析动画字符串
     * 格式: "type" 或 "type speed" 或 "type speed color"
     */
    private _parseAnimation;
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
    private _parseParticles;
    get fromConfig(): ConnectionConfig;
    get toConfig(): ConnectionConfig;
    get animationConfig(): AnimationConfig;
    get particleConfig(): ParticleConfig;
    get fromFace(): FaceType_2;
    get fromPosition(): PositionType_2;
    get toFace(): FaceType_2;
    get toPosition(): PositionType_2;
    static styles: CSSResult;
    private _anglesHandler;
    connectedCallback(): void;
    disconnectedCallback(): void;
    private _setupObservers;
    private _cleanupObservers;
    private _scheduleUpdate;
    private _findEntities;
    updated(changedProperties: Map<string, unknown>): void;
    /**
     * 更新 CSS 变量
     */
    private _updateCSSVariables;
    /**
     * 获取面的法线对应的主轴
     */
    private _getFaceAxis;
    /**
     * 解析路由顺序（仅用于中间段）
     */
    private _parseRoute;
    /**
     * 计算 3D 路径段
     *
     * 路由策略：
     * 1. 如果 perpendicularLength > 0：从起点沿起点面的法线方向出发
     * 2. 中间按用户指定的轴顺序走线（自动补充缺失的轴）
     * 3. 如果 perpendicularLength > 0：沿终点面的法线方向进入终点
     */
    private _updatePath;
    /**
     * 启动粒子动画
     */
    private _startParticleAnimation;
    /**
     * 停止粒子动画
     */
    private _stopParticleAnimation;
    /**
     * 更新粒子状态
     */
    private _updateParticles;
    /**
     * 根据进度（0-1）计算粒子在 3D 空间中的位置
     */
    private _getPositionAtProgress;
    /**
     * 获取彩虹颜色（rainbow 特效）
     */
    private _getRainbowColor;
    /**
     * 渲染单个粒子
     */
    private _renderParticle;
    /**
     * 渲染带拖尾的粒子
     */
    private _renderTrailParticle;
    /**
     * 计算线段的 3D 变换
     *
     * 线段默认沿 X 轴正方向绘制（从原点向右），通过旋转来改变方向
     * 在等距空间中：
     * - X 轴：rotateZ(0°) 或 rotateZ(180°)
     * - Y 轴：rotateZ(90°) 或 rotateZ(-90°)
     * - Z 轴：rotateX(-90°) 或 rotateX(90°)（绕 X 轴旋转，让线段朝上或朝下）
     */
    private _getSegmentTransform;
    private _getLineStyleClass;
    private _getAnimationClass;
    render(): TemplateResult<1>;
}

/**
 * 等距操作台 - 顶面向前倾斜
 *
 * 类似立方体但顶面倾斜，模拟操作台/控制台的外观
 * 由倾斜顶面 + 前面组成（隐藏右面）
 */
export declare class IsoConsoleFront extends IsoCube {
    static styles: CSSResult[];
}

/**
 * 等距操作台 - 顶面向右倾斜
 *
 * 类似立方体但顶面倾斜，模拟操作台/控制台的外观
 * 由倾斜顶面 + 右面组成（隐藏前面）
 */
export declare class IsoConsoleRight extends IsoCube {
    static styles: CSSResult[];
}

/**
 * 等距立方体 Web Component
 * 继承自 IsoEntity 基类，渲染立方体形状
 */
export declare class IsoCube extends IsoEntity {
    static styles: CSSResult[];
    render(): TemplateResult<1>;
}

/**
 * 等距实体基类 Web Component
 *
 * 核心思路：场景层已经做了 3D 旋转，实体只需要用 translate3d(x, y, z) 定位
 * 浏览器会自动根据 3D 空间位置计算遮挡关系
 *
 * 子类需要：
 * 1. 实现 render() 方法渲染具体形状
 * 2. 可选覆盖 static styles 添加额外样式
 */
export declare abstract class IsoEntity extends LitElement {
    x: number;
    y: number;
    z: number;
    row: number | null;
    col: number | null;
    gridSize: number;
    width: number;
    height: number;
    depth: number;
    topColor: "#6C9BCF";
    frontColor: "#4A7AB0";
    rightColor: "#3A6691";
    entityId: string;
    noPointer: boolean;
    protected _scene: HTMLElement | null;
    /** 基类样式，子类可通过 static styles 扩展 */
    static baseStyles: CSSResult;
    connectedCallback(): void;
    updated(changedProperties: Map<string, unknown>): void;
    /**
     * 更新位置
     */
    updatePosition(): void;
    /**
     * 获取实体中心点的屏幕坐标
     */
    getConnectionPoint(): {
        x: number;
        y: number;
    };
    /**
     * 获取指定面和位置的等距坐标
     */
    getFaceConnectionPoint(face?: FaceType, position?: PositionType): {
        x: number;
        y: number;
        z: number;
    };
    /**
     * 获取指定面和位置的屏幕坐标
     */
    getFaceConnectionPointScreen(face?: FaceType, position?: PositionType): {
        x: number;
        y: number;
        z: number;
    };
    /** 子类必须实现渲染方法 */
    abstract render(): TemplateResult;
}

/**
 * 等距引擎主类
 * 提供创建和管理等距场景的统一 API
 */
declare class IsometricEngine {
    /** 引擎版本 */
    static readonly VERSION = "0.1.0";
    /** 场景集合 */
    private scenes;
    /** 连线集合 */
    private connectors;
    /** 浮层集合 */
    private tooltips;
    /** 光影系统 */
    private lightingSystem;
    constructor();
    /**
     * 创建场景
     */
    createScene(container: HTMLElement, options?: SceneOptions): Scene;
    /**
     * 创建实体
     */
    createEntity(options?: EntityOptions): Entity;
    /**
     * 创建复合实体
     */
    createCompositeEntity(options?: CompositeEntityOptions): CompositeEntity;
    /**
     * 创建连线
     */
    createConnector(from: Entity, to: Entity, options?: ConnectorOptions): Connector;
    /**
     * 将连线添加到场景
     */
    addConnectorToScene(connector: Connector, scene: Scene): void;
    /**
     * 创建浮层
     */
    createTooltip(target: Entity, options: TooltipOptions): Tooltip;
    /**
     * 将浮层添加到场景
     */
    addTooltipToScene(tooltip: Tooltip, scene: Scene): void;
    /**
     * 添加光源
     */
    addLight(options: LightOptions): Light;
    /**
     * 获取光影系统
     */
    getLightingSystem(): LightingSystem;
    /**
     * 注册自定义特效
     */
    registerEffect(name: string, definition: Parameters<typeof effectManager.register>[1]): this;
    /**
     * 获取所有可用特效
     */
    getAvailableEffects(): string[];
    /**
     * 销毁引擎
     */
    destroy(): void;
}
export { IsometricEngine }
export default IsometricEngine;

/**
 * 等距事件对象
 */
export declare interface IsometricEvent<T = unknown> {
    /** 事件类型 */
    type: EventType;
    /** 事件目标 */
    target: T;
    /** 原生 DOM 事件 */
    originalEvent: Event;
    /** 等距坐标位置 */
    position: IsometricPosition;
    /** 屏幕坐标位置 */
    screenPosition: ScreenPosition;
    /** 阻止默认行为 */
    preventDefault: () => void;
    /** 停止传播 */
    stopPropagation: () => void;
}

/**
 * 等距事件对象实现
 */
export declare class IsometricEventImpl<T = unknown> {
    readonly type: EventType;
    readonly target: T;
    readonly originalEvent: Event;
    readonly position: IsometricPosition;
    readonly screenPosition: ScreenPosition;
    private _defaultPrevented;
    private _propagationStopped;
    constructor(type: EventType, target: T, originalEvent: Event, position: IsometricPosition, screenPosition: ScreenPosition);
    preventDefault(): void;
    stopPropagation(): void;
    get defaultPrevented(): boolean;
    get propagationStopped(): boolean;
}

/**
 * 等距坐标位置
 */
export declare interface IsometricPosition {
    /** 等距 X 坐标 */
    x: number;
    /** 等距 Y 坐标 */
    y: number;
    /** 高度层级 (Z 轴) */
    z: number;
}

/**
 * 等距单面 Web Component
 *
 * 只渲染一个水平面，可用于地板等
 */
export declare class IsoPlane extends LitElement {
    x: number;
    y: number;
    z: number;
    width: number;
    height: number;
    color: string;
    entityId: string;
    noPointer: boolean;
    static styles: CSSResult;
    connectedCallback(): void;
    updated(changedProperties: Map<string, unknown>): void;
    private _updateStyles;
    private _updatePosition;
    render(): TemplateResult<1>;
}

/** 等距坐标点 */
declare interface IsoPoint {
    x: number;
    y: number;
    z: number;
}

/**
 * 等距场景 Web Component
 * 作为实体和连线的容器
 *
 * 核心思路：只在场景层做一次 3D 旋转，子元素只需要用 translate3d 定位
 * 浏览器会自动根据 3D 空间位置计算遮挡关系
 *
 * 使用方式:
 * <iso-scene center-origin>
 *   <iso-cube ...></iso-cube>
 *   <iso-connector ...></iso-connector>
 * </iso-scene>
 */
export declare class IsoScene extends LitElement {
    centerOrigin: boolean;
    width: number;
    height: number;
    originX: number;
    originY: number;
    perspective: number;
    _originPosition: {
        x: number;
        y: number;
    };
    private _rotateX;
    private _rotateZ;
    private _perspective;
    private _resizeObserver;
    private _anglesHandler;
    static styles: CSSResult;
    connectedCallback(): void;
    disconnectedCallback(): void;
    firstUpdated(): void;
    updated(changedProperties: Map<string, unknown>): void;
    private _updateOrigin;
    private _updateConnectors;
    /**
     * 获取原点位置
     */
    getOriginPosition(): {
        x: number;
        y: number;
    };
    /**
     * 获取所有实体
     */
    getEntities(): IsoEntity[];
    /**
     * 根据 ID 获取实体
     */
    getEntityById(id: string): IsoEntity | null;
    /**
     * 获取所有连线
     */
    getConnectors(): IsoConnector[];
    render(): TemplateResult<1>;
}

/**
 * 等距坐标转网格坐标
 */
export declare function isoToGrid(pos: IsometricPosition, cellSize?: number, origin?: IsometricPosition): GridPosition;

/**
 * 等距坐标转屏幕坐标
 *
 * CSS 3D 变换：rotateX(60deg) rotateZ(45deg)
 *
 * 变换顺序（从右到左应用）：
 * 1. rotateZ(45deg): 在 XY 平面旋转
 *    x' = x*cos(45) - y*sin(45)
 *    y' = x*sin(45) + y*cos(45)
 *    z' = z
 *
 * 2. rotateX(60deg): 绕 X 轴旋转，压缩 Y 轴
 *    x'' = x'
 *    y'' = y'*cos(60) - z'*sin(60)
 *    (屏幕 Y 轴向下为正)
 *
 * 最终屏幕坐标：
 *    screenX = x'' = (x - y) * cos(45)
 *    screenY = y'' = (x + y) * sin(45) * cos(60) - z * sin(60)
 */
export declare function isoToScreen(pos: IsometricPosition, scale?: number, origin?: ScreenPosition): ScreenPosition;

/**
 * 光源
 */
export declare class Light {
    /** 唯一标识符 */
    readonly id: string;
    /** 光源位置 */
    position: IsometricPosition;
    /** 光源颜色 */
    color: string;
    /** 光源强度 */
    intensity: number;
    /** 光源类型 */
    type: 'point' | 'directional' | 'ambient';
    constructor(options: LightOptions);
}

/**
 * 光影系统
 * 管理场景中的光源和阴影效果
 */
export declare class LightingSystem {
    /** 光源集合 */
    private lights;
    /** 环境光颜色 */
    private _ambientColor;
    /** 阴影启用状态 */
    private shadowsEnabled;
    /** 阴影模糊度 */
    private shadowBlur;
    /** 阴影偏移 */
    private shadowOffset;
    /**
     * 添加光源
     */
    addLight(options: LightOptions): Light;
    /**
     * 移除光源
     */
    removeLight(id: string): boolean;
    /**
     * 获取光源
     */
    getLight(id: string): Light | undefined;
    /**
     * 获取所有光源
     */
    getAllLights(): Light[];
    /**
     * 设置环境光
     */
    setAmbientLight(color: string): this;
    /**
     * 获取环境光颜色
     */
    getAmbientColor(): string;
    /**
     * 启用/禁用阴影
     */
    setShadowsEnabled(enabled: boolean): this;
    /**
     * 设置阴影参数
     */
    setShadowParams(blur: number, offset: {
        x: number;
        y: number;
    }): this;
    /**
     * 计算元素在指定位置受到的光照效果
     */
    calculateLighting(position: IsometricPosition): Partial<CSSStyleDeclaration>;
    /**
     * 生成阴影 CSS
     */
    generateShadowCSS(position: IsometricPosition): string;
    /**
     * 清除所有光源
     */
    clear(): void;
    /**
     * 销毁光影系统
     */
    destroy(): void;
}

/**
 * 光源配置
 */
export declare interface LightOptions {
    /** 光源位置 */
    position: IsometricPosition;
    /** 光源颜色 */
    color?: string;
    /** 光源强度 (0-1) */
    intensity?: number;
    /** 光源类型 */
    type?: 'point' | 'directional' | 'ambient';
}

/**
 * 解析十六进制颜色字符串为 RGB
 */
export declare function parseHexColor(color: string): RGB;

/** 解析后的粒子配置 */
declare interface ParticleConfig {
    enabled: boolean;
    color: string;
    size: number;
    rate: number;
    speed: number;
    effect: ParticleEffect;
    direction: ParticleDirection;
    trailLength: number;
}

declare type ParticleDirection = 'forward' | 'backward' | 'bidirectional';

declare type ParticleEffect = 'none' | 'glow' | 'trail' | 'pulse' | 'rainbow' | 'spark';

/**
 * 路径计算器
 * 负责计算等距连线的路径
 */
export declare class PathCalculator {
    private zOffset;
    constructor(zOffset?: number);
    /**
     * 设置 z-index 偏移
     */
    setZOffset(offset: number): void;
    /**
     * 解析路由顺序
     */
    parseRoute(route: string): RouteAxis[];
    /**
     * 等距偏移转屏幕偏移
     * 使用 CSS 3D 变换矩阵
     */
    private isoOffsetToScreen;
    /**
     * 计算屏幕路径（使用屏幕坐标）
     */
    calculateScreenPath(fromScreen: ScreenPoint3D, _toScreen: ScreenPoint3D, fromIso: IsoPoint, toIso: IsoPoint, route: string, sceneOffset: ScreenPoint): PathSegment[];
    /**
     * 等距坐标转屏幕坐标（简化版，不带 origin）
     * @deprecated 使用 calculateScreenPath 代替
     */
    private isoToScreenSimple;
    /**
     * 计算等距路径（沿轴向走线）
     * @deprecated 使用 calculateScreenPath 代替
     */
    calculateIsometricPath(fromIso: IsoPoint, toIso: IsoPoint, route: string, sceneOffset: ScreenPoint): PathSegment[];
    /**
     * 生成 SVG 路径
     */
    generatePath(segments: PathSegment[]): {
        paths: PathWithZIndex[];
        arrowTransform: string;
    };
}

/** 路径段 */
declare interface PathSegment {
    screenPoints: ScreenPoint[];
    avgZ: number;
}

/** 带 z-index 的路径 */
declare interface PathWithZIndex {
    path: string;
    zIndex: number;
}

export declare type PositionType = 'tl' | 'tc' | 'tr' | 'ml' | 'mc' | 'mr' | 'bl' | 'bc' | 'br';

declare type PositionType_2 = 'tl' | 'tc' | 'tr' | 'ml' | 'mc' | 'mr' | 'bl' | 'bc' | 'br';

/**
 * 重置所有计数器
 */
export declare function resetAllIdCounters(): void;

/**
 * 重置指定前缀的计数器
 * @param prefix ID 前缀
 */
export declare function resetIdCounter(prefix: string): void;

/**
 * RGB 颜色结构
 */
export declare interface RGB {
    r: number;
    g: number;
    b: number;
}

/**
 * RGB 转十六进制颜色字符串
 */
export declare function rgbToHex(rgb: RGB): string;

/** rotateX 角度（度）- 等距倾斜角 */
export declare const ROTATE_X_DEG: 60;

/** rotateX 角度（弧度） */
export declare const ROTATE_X_RAD: number;

/** rotateZ 角度（度） */
export declare const ROTATE_Z_DEG: 45;

/** rotateZ 角度（弧度） */
export declare const ROTATE_Z_RAD: number;

/** 路由方向类型 */
declare type RouteAxis = 'x' | 'y' | 'z';

/** 运行时角度配置（可动态修改） */
export declare const runtimeAngles: {
    rotateX: number;
    rotateZ: number;
};

/**
 * 场景管理器
 * 负责管理场景容器、实体集合和渲染调度
 */
export declare class Scene {
    /** 场景容器元素 */
    private container;
    /** 场景内部包装器 */
    private wrapper;
    /** 等距视角容器 */
    private isoContainer;
    /** 坐标变换器 */
    private transform;
    /** 场景中的实体集合 */
    private entities;
    /** 场景配置 */
    private options;
    constructor(container: HTMLElement, options?: SceneOptions);
    /**
     * 创建场景内部包装器
     */
    private createWrapper;
    /**
     * 应用容器样式
     */
    private applyContainerStyles;
    /**
     * 添加实体到场景
     */
    add(entity: Entity): this;
    /**
     * 从场景移除实体
     */
    remove(entity: Entity): this;
    /**
     * 更新实体的 z-index
     */
    updateEntityZIndex(entity: Entity): void;
    /**
     * 更新所有实体的 z-index
     */
    updateAllZIndices(): void;
    /**
     * 获取坐标变换器
     */
    getTransform(): Transform;
    /**
     * 获取场景包装器元素
     */
    getWrapper(): HTMLElement;
    /**
     * 获取场景容器元素
     */
    getContainer(): HTMLElement;
    /**
     * 将屏幕坐标转换为等距坐标
     */
    screenToIso(screen: ScreenPosition, z?: number): IsometricPosition;
    /**
     * 将等距坐标转换为屏幕坐标
     */
    isoToScreen(pos: IsometricPosition): ScreenPosition;
    /**
     * 更新场景配置
     */
    update(options: Partial<SceneOptions>): void;
    /**
     * 获取场景中的所有实体
     */
    getEntities(): Entity[];
    /**
     * 清空场景
     */
    clear(): void;
    /**
     * 销毁场景
     */
    destroy(): void;
    /**
     * 设置场景原点（通常设置为容器中心）
     */
    centerOrigin(): void;
}

/**
 * 场景配置选项
 */
export declare interface SceneOptions {
    /** 等距角度 (默认 45°) */
    angle?: number;
    /** 透视距离 (默认 1000px) */
    perspective?: number;
    /** 缩放比例 */
    scale?: number;
    /** 原点位置 */
    origin?: ScreenPosition;
}

/** 屏幕坐标点 */
declare interface ScreenPoint {
    x: number;
    y: number;
}

/** 屏幕坐标点（带 z 用于 z-index） */
declare interface ScreenPoint3D {
    x: number;
    y: number;
    z: number;
}

/**
 * 屏幕坐标
 */
export declare interface ScreenPosition {
    x: number;
    y: number;
}

/**
 * 屏幕坐标转等距坐标（假设 z = 0）
 *
 * 逆变换：
 * screenX = (x - y) * COS_Z
 * screenY = (x + y) * SIN_Z * COS_X - z * SIN_X
 *
 * 当 z = 0 时：
 * screenX = (x - y) * COS_Z
 * screenY = (x + y) * SIN_Z * COS_X
 *
 * 解方程：
 * x - y = screenX / COS_Z
 * x + y = screenY / (SIN_Z * COS_X)
 *
 * x = (screenX/COS_Z + screenY/(SIN_Z*COS_X)) / 2
 * y = (screenY/(SIN_Z*COS_X) - screenX/COS_Z) / 2
 */
export declare function screenToIso(screen: ScreenPosition, z?: number, scale?: number, origin?: ScreenPosition): IsometricPosition;

export declare const SIN_ANGLE: number;

export declare const SIN_X: number;

export declare const SIN_Z: number;

/**
 * 三维尺寸
 */
export declare interface Size3D {
    width: number;
    height: number;
    depth: number;
}

/**
 * 浮层组件
 * 用于显示实体的悬浮提示信息
 */
export declare class Tooltip {
    /** 唯一标识符 */
    readonly id: string;
    /** DOM 元素 */
    protected element: HTMLElement;
    /** 目标实体 */
    protected target: Entity;
    /** 所属场景 */
    protected scene: Scene | null;
    /** 配置选项 */
    protected options: TooltipOptions;
    /** 是否可见 */
    protected visible: boolean;
    /** 事件监听器引用 */
    private boundHandlers;
    constructor(target: Entity, options: TooltipOptions);
    /**
     * 创建浮层 DOM 元素
     */
    protected createElement(): HTMLElement;
    /**
     * 绑定触发事件
     */
    protected bindTriggerEvents(): void;
    /**
     * 设置内容
     */
    setContent(content: string | HTMLElement): this;
    /**
     * 更新位置
     */
    protected updatePosition(): void;
    /**
     * 显示浮层
     */
    show(): this;
    /**
     * 隐藏浮层
     */
    hide(): this;
    /**
     * 切换显示状态
     */
    toggle(): this;
    /**
     * 附加到场景
     */
    attachToScene(scene: Scene): void;
    /**
     * 从场景分离
     */
    detachFromScene(): void;
    /**
     * 获取 DOM 元素
     */
    getElement(): HTMLElement;
    /**
     * 是否可见
     */
    isVisible(): boolean;
    /**
     * 销毁浮层
     */
    destroy(): void;
}

/**
 * 浮层配置选项
 */
export declare interface TooltipOptions {
    /** 浮层内容 */
    content: string | HTMLElement;
    /** 显示位置 */
    position?: 'top' | 'bottom' | 'left' | 'right';
    /** 偏移量 */
    offset?: ScreenPosition;
    /** 触发方式 */
    trigger?: 'hover' | 'click' | 'manual';
    /** 自定义类名 */
    className?: string;
}

/**
 * 等距坐标变换核心类
 * 负责等距坐标与屏幕坐标之间的转换
 */
export declare class Transform {
    /** 等距角度 (弧度) */
    private angle;
    /** 透视距离 */
    private perspective;
    /** 缩放比例 */
    private scale;
    /** 原点位置 */
    private origin;
    constructor(options?: SceneOptions);
    /**
     * 将等距坐标转换为屏幕坐标
     */
    isoToScreen(pos: IsometricPosition): ScreenPosition;
    /**
     * 将屏幕坐标转换为等距坐标 (假设 z = 0)
     */
    screenToIso(screen: ScreenPosition, z?: number): IsometricPosition;
    /**
     * 获取 CSS 3D Transform 字符串
     */
    getCSS3DTransform(pos: IsometricPosition): string;
    /**
     * 获取等距平面的 CSS Transform
     */
    getIsometricPlaneTransform(): string;
    /**
     * 获取透视容器的 CSS 样式
     */
    getPerspectiveStyle(): Partial<CSSStyleDeclaration>;
    /**
     * 计算两个等距坐标之间的距离
     */
    distance(a: IsometricPosition, b: IsometricPosition): number;
    /**
     * 计算 Z-Index 排序值
     */
    calculateZIndex(pos: IsometricPosition): number;
    /**
     * 更新变换参数
     */
    update(options: Partial<SceneOptions>): void;
    /**
     * 获取当前配置
     */
    getConfig(): SceneOptions;
}

/** 更新运行时角度 */
export declare function updateAngles(rotateX: number, rotateZ: number): void;

export { }
